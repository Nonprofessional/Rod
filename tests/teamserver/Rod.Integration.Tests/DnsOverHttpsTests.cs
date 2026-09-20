using System.Net;
using System.Net.Http.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Implants;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;
using Rod.Transport.Listeners.Dns;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the DNS grammar's second carriage (architecture.md
/// Sec 8): DNS-over-HTTPS per RFC 8484, the same TXT contact wire the UDP
/// listener answers, as DNS wire messages over HTTP bodies. A doh listener
/// entry owns the route -- its public endpoint is the zone it answers for
/// -- and the arrival port resolves which listener answers. The acceptance
/// bar: a real listener entry on a real socket, real DNS wire in and out
/// through both the GET and POST shapes, the zone's answer codes exactly
/// the UDP listener's, and a socket no doh listener owns answering 404.
/// </summary>
public class DnsOverHttpsTests
{
    private const string Zone = "doh.example.test";

    [Fact]
    public async Task DohListener_AnswersTheDnsGrammarOverRfc8484Bodies()
    {
        await using var env = await TestEnv.StartAsync();
        await env.LoginAsync();
        var engagementId = await env.CreateEngagementAsync();

        // The listener entry: transport doh, the public endpoint its zone,
        // the bind a real socket the runtime manager opens.
        var dohPort = TestSupport.GetFreeTcpPort();
        var created = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "doh-front", Transport: "doh",
                BindAddress: $"127.0.0.1:{dohPort}", PublicEndpoint: Zone));
        created.EnsureSuccessStatusCode();
        var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        Assert.NotNull(listener);
        Assert.Equal("running", listener!.State);
        env.Doh.BaseAddress = new Uri($"https://127.0.0.1:{dohPort}");

        // POST: an in-zone poll for an implant with no session -- the
        // documented NOERROR-empty answer, exactly the UDP listener's.
        var pollName = DnsContactNames.PollName(ImplantId.New(), Zone);
        var post = await env.Doh.PostAsync($"{DnsOverHttpsEndpoints.Route}",
            new ByteArrayContent(Query(pollName)));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal(DnsOverHttpsEndpoints.MediaType, post.Content.Headers.ContentType?.MediaType);
        var postAnswer = await post.Content.ReadAsByteArrayAsync();
        Assert.Equal(0, ResponseCode(postAnswer)); // NOERROR
        Assert.Equal(0, AnswerCount(postAnswer)); // no session, no tasking

        // GET: the same query under the urlsafe-base64 dns parameter.
        var encoded = ToUrlSafeBase64(Query(pollName));
        var get = await env.Doh.GetAsync($"{DnsOverHttpsEndpoints.Route}?dns={encoded}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var getAnswer = await get.Content.ReadAsByteArrayAsync();
        Assert.Equal(0, ResponseCode(getAnswer));
        Assert.Equal(0, AnswerCount(getAnswer));

        // The zone's answer codes: a foreign zone is REFUSED (this listener
        // is not an open resolver), an in-zone non-contact is NXDOMAIN.
        var foreign = await env.Doh.PostAsync($"{DnsOverHttpsEndpoints.Route}",
            new ByteArrayContent(Query("p.other.example.test")));
        var foreignAnswer = await foreign.Content.ReadAsByteArrayAsync();
        Assert.Equal(5, ResponseCode(foreignAnswer)); // REFUSED

        var notContact = await env.Doh.PostAsync($"{DnsOverHttpsEndpoints.Route}",
            new ByteArrayContent(Query($"ordinary.{Zone}")));
        var notContactAnswer = await notContact.Content.ReadAsByteArrayAsync();
        Assert.Equal(3, ResponseCode(notContactAnswer)); // NXDOMAIN

        // A socket no doh listener owns: the route is not served there --
        // an ordinary 404, so a prober learns nothing.
        var elsewhere = await env.Http.GetAsync($"{DnsOverHttpsEndpoints.Route}?dns={encoded}");
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    // Builds one TXT query wire message for a name, the hand-rolled shape
    // the DNS codec's own round-trip test pins: header, single question,
    // and the EDNS0 OPT record a real resolver sends.
    private static byte[] Query(string name)
    {
        var buffer = new List<byte>(128);
        buffer.Add(0x12); buffer.Add(0x34); // id
        buffer.Add(0); buffer.Add(1); // flags: query, recursion desired
        buffer.Add(0); buffer.Add(1); // qdcount
        buffer.Add(0); buffer.Add(0);
        buffer.Add(0); buffer.Add(0);
        buffer.Add(0); buffer.Add(1); // arcount: the OPT record

        foreach (var label in name.Split('.'))
        {
            buffer.Add((byte)label.Length);
            buffer.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        buffer.Add(0);
        buffer.Add(0); buffer.Add((byte)DnsCodec.TxtType);
        buffer.Add(0); buffer.Add(1);

        // OPT: root name, type 41, class = payload size, no data.
        buffer.Add(0);
        buffer.Add(0); buffer.Add(41);
        buffer.Add(4); buffer.Add(208); // 1232
        buffer.Add(0); buffer.Add(0); buffer.Add(0); buffer.Add(0);
        buffer.Add(0); buffer.Add(0);
        return buffer.ToArray();
    }

    // The DNS header's rcode: the low nibble of byte 3.
    private static int ResponseCode(byte[] response) => response[3] & 0x0F;

    // The DNS header's answer count: bytes 6-7, network order.
    private static int AnswerCount(byte[] response) => (response[6] << 8) | response[7];

    private static string ToUrlSafeBase64(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // A real-socket host: the operator front plus the runtime listener
    // manager's real Kestrel reloader, and a DoH client pinning the
    // engagement CA the dynamic TLS endpoints present.
    private sealed class TestEnv : IAsyncDisposable
    {
        private Rod.CoreState.Pki.IImplantCertificateAuthority _ca = null!;

        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public HttpClient Doh { get; private set; } = null!;
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            env.HttpPort = TestSupport.GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseRodListeners(new List<ListenerConfig>
                    {
                        new("operator-http", "http", $"127.0.0.1:{env.HttpPort}", $"http://127.0.0.1:{env.HttpPort}"),
                    }))
                .Build();
            await env.Host.StartAsync();

            env._ca = env.Host.Services.GetRequiredService<Rod.CoreState.Pki.IImplantCertificateAuthority>();
            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}"),
            };
            env.Doh = new HttpClient(BuildPinnedHandler(env._ca.GetCaCertificate()));
            return env;
        }

        public async Task LoginAsync() => await AuthenticatedHost.LoginAsync(Http);

        public async Task<string> CreateEngagementAsync()
        {
            var response = await Http.PostAsJsonAsync("/engagements",
                new EngagementEndpoints.CreateEngagementRequest(Name: "Operation DoH"));
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
            return created!.EngagementId;
        }

        private static HttpClientHandler BuildPinnedHandler(
            System.Security.Cryptography.X509Certificates.X509Certificate2 ca)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, chain, _) =>
                {
                    chain!.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                    chain!.ChainPolicy.VerificationFlags = System.Security.Cryptography.X509Certificates.X509VerificationFlags.AllowUnknownCertificateAuthority;
                    chain!.ChainPolicy.ExtraStore.Add(ca);
                    return chain.Build(cert!);
                },
            };
            return handler;
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            Doh?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }
}
