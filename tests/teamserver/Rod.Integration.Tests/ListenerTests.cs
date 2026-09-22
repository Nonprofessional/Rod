using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Net.Security;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.CoreState.Sessions;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: a listener accepts an implant connection end-to-end.
/// Drives the full slice through real Kestrel sockets configured via
/// <see cref="TransportHost.UseRodListeners"/> -- the listener abstraction
/// that fronts the same endpoints, with the bind address decoupled
/// from the public endpoint (architecture.md Sec 8). This is the listener-centric
/// counterpart to the socket-level handshake tests: the connection
/// terminates through a named, registered listener rather than a bespoke socket.
/// </summary>
public class ListenerTests
{
    [Fact]
    public async Task HttpListener_AcceptsImplantConnection_EndToEnd()
    {
        // An engagement's HTTP listener is created through the scoped API and
        // serves the implant enrollment endpoint over plain HTTP (no client
        // certificate). This is the end-to-end AC for the HTTP transport: an
        // implant enrolls through the listener.
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "operator-http",
            Transport: "http",
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));

        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);

        // The listener is recorded with its bind address and public endpoint.
        var created = await env.Http.PostAsJsonAsync($"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "http-1", Transport: "http",
                BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
                PublicEndpoint: "http://c2.example.test"));
        created.EnsureSuccessStatusCode();
        var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        Assert.NotNull(listener);
        Assert.Equal("http-1", listener!.Name);
        Assert.Equal("http", listener.Transport);
        Assert.Equal("http://c2.example.test", listener.PublicEndpoint);
        Assert.Equal("running", listener.State);

        // And the listener accepts an implant connection end-to-end: enroll
        // against the listener's bind address and receive the bound
        // certificate. The operator front itself refuses implant ingress --
        // the engagement's own listener is the only path in.
        var secret = await MintTokenAsync(env.Http, engagementId);
        using var scoped = new HttpClient { BaseAddress = new Uri($"http://{listener.BindAddress}") };
        var response = await scoped.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(DeployTokenSecret: secret, Class: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
        Assert.NotNull(enrolled);
        Assert.Equal(EnrollStatus.Ok, enrolled!.Status);
        Assert.False(string.IsNullOrWhiteSpace(enrolled.ImplantId));
    }

    [Fact]
    public async Task ListenerDelete_GuardsLiveImplants_EnrolledThroughIt()
    {
        // The two-step delete: a listener that live implants enrolled through is
        // load-bearing ingress, so deleting it refuses with the count until the
        // caller forces. The enrollment also stamps which listener carried it
        // and the kill date the implant reported (null when it reported none --
        // the open-ended build).
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "operator-http",
            Transport: "http",
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));

        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);

        var created = await env.Http.PostAsJsonAsync($"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "http-guard", Transport: "http",
                BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
                PublicEndpoint: "http://c2.example.test"));
        created.EnsureSuccessStatusCode();
        var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        Assert.NotNull(listener);

        // One open-ended enrollment and one with a reported fuse, both through
        // the listener's own socket so the ingress stamp lands.
        var openSecret = await MintTokenAsync(env.Http, engagementId);
        var fuse = DateTimeOffset.UtcNow.AddDays(45);
        using var scoped = new HttpClient { BaseAddress = new Uri($"http://{listener.BindAddress}") };
        var openEnroll = await scoped.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(DeployTokenSecret: openSecret));
        openEnroll.EnsureSuccessStatusCode();
        var fusedEnroll = await scoped.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(DeployTokenSecret: await MintTokenAsync(env.Http, engagementId), KillDate: fuse.ToString("O")));
        fusedEnroll.EnsureSuccessStatusCode();

        var implants = await env.Http.GetFromJsonAsync<ImplantEndpoints.ImplantResponse[]>(
            $"/engagements/{engagementId}/implants");
        Assert.NotNull(implants);
        Assert.All(implants!, i => Assert.Equal(listener!.Id, i.EnrolledViaListenerId));
        Assert.Contains(implants!, i => i.KillDate is null);
        Assert.Contains(implants!, i => i.KillDate == fuse);

        // The guard: an unforced delete refuses and names the live dependents.
        var refused = await env.Http.DeleteAsync($"/engagements/{engagementId}/listeners/{listener.Id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<Problem>();
        Assert.NotNull(problem);
        Assert.Contains("2 live implants", problem!.Error);

        // The force path: an explicit confirmation deletes anyway.
        var forced = await env.Http.DeleteAsync($"/engagements/{engagementId}/listeners/{listener.Id}?force=true");
        Assert.Equal(HttpStatusCode.NoContent, forced.StatusCode);
    }

    [Fact]
    public async Task ListenerListing_RendersTheTransportWireNames()
    {
        // The stable label contract: the listing renders each transport's
        // wire name -- plain for the single-word entries (every current
        // transport), kebab-cased for any multi-word entry to come, never
        // one mashed lower-cased word. The operator UI renders the listing's
        // string verbatim, so this is the UI's name too.
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "operator-http",
            Transport: "http",
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));

        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);
        await CreateListenerAsync(env.Http, engagementId, "tls-1", "https", "https://c2.example.test");
        await CreateListenerAsync(env.Http, engagementId, "http-1", "http", "http://c2.example.test");

        var listeners = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>(
            $"/engagements/{engagementId}/listeners");
        Assert.NotNull(listeners);
        var transports = listeners!.ToDictionary(l => l.Name, l => l.Transport);
        Assert.Equal("https", transports["tls-1"]);
        Assert.Equal("http", transports["http-1"]);
    }

    [Fact]
    public async Task Listener_BindAddress_IsDecoupled_FromPublicEndpoint()
    {
        // architecture.md Sec 8: the listener and the public endpoint are decoupled
        // so a burned redirector is replaceable without backend change. Here the
        // public endpoint is a redirector host that differs from the bind address;
        // the listener records both independently. The public endpoint is data, not
        // the socket -- swapping it (a different redirector) never touches the bind.
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "operator-http",
            Transport: "http",
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));

        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);
        var listener = await CreateListenerAsync(
            env.Http, engagementId, "tls-redirected", "https", "https://redirect-a.example.test");

        // The record carries the listener with its decoupled public endpoint.
        Assert.Equal("https://redirect-a.example.test", listener.PublicEndpoint);
        Assert.NotEqual(listener.BindAddress, listener.PublicEndpoint);
        Assert.StartsWith("127.0.0.1:", listener.BindAddress);
    }

    [Fact]
    public async Task GetListener_Returns404_ForUnknownId()
    {
        await using var env = await TestEnv.StartAsync(DefaultHttpListener());

        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);
        var response = await env.Http.GetAsync($"/engagements/{engagementId}/listeners/{ListenerId.New()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static ListenerConfig DefaultHttpListener()
        => new("http-default", "http", $"127.0.0.1:{TestSupport.GetFreeTcpPort()}", "http://localhost");

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Smokeshow"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    private static async Task<string> MintTokenAsync(HttpClient client, string engagementId)
    {
        var mintResponse = await client.PostAsync($"/engagements/{engagementId}/deploy-tokens", content: null);
        mintResponse.EnsureSuccessStatusCode();
        var token = await mintResponse.Content.ReadFromJsonAsync<EngagementEndpoints.DeployTokenResponse>();
        return token!.Secret;
    }

    private static async Task<ListenerEndpoints.ListenerResponse> CreateListenerAsync(
        HttpClient client, string engagementId, string name, string transport, string publicEndpoint)
    {
        var response = await client.PostAsJsonAsync($"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: name, Transport: transport,
                BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
                PublicEndpoint: publicEndpoint));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        return created!;
    }

    private static async Task<Implant> EnrollImplantAsync(
        IImplantRepository implants, TimeProvider clock, ImplantClass @class = ImplantClass.Implant)
    {
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(),
            now.AddDays(30), @class, now);
        await implants.SaveAsync(implant);
        return implant;
    }

    private static Frame HandshakeFrame(ImplantId implant, int major, int minor)
    {
        var request = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = major, Minor = minor },
            ImplantId = implant.ToString(),
            Capabilities = { "shell.exec", "file.push" },
        };
        return new Frame { Payload = ByteString.CopyFrom(request.ToByteArray()) };
    }

    private static HandshakeResponse ParseResponse(Frame frame)
        => HandshakeResponse.Parser.ParseFrom(frame.Payload);

    /// <summary>
    /// A real Kestrel teamserver whose listeners are bound via
    /// <see cref="TransportHost.UseRodListeners"/>, plus a plain-HTTP operator API.
    /// Disposed to tear the listeners down.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public string HttpBind { get; private set; } = "";
        public string HttpsBind { get; private set; } = "";

        public static async Task<TestEnv> StartAsync(params ListenerConfig[] listeners)
        {
            var env = new TestEnv();

            // Pick free ports up front so the config's bind addresses match the
            // sockets Kestrel opens, and so tests can dial them.
            var httpListener = listeners.FirstOrDefault(l => l.Transport == "http");
            var httpsFront = listeners.FirstOrDefault(l => l.Transport == "https");
            var rewritten = new List<ListenerConfig>();
            if (httpListener is not null)
            {
                env.HttpBind = $"127.0.0.1:{TestSupport.GetFreeTcpPort()}";
                rewritten.Add(httpListener with { BindAddress = env.HttpBind });
            }
            if (httpsFront is not null)
            {
                env.HttpsBind = $"127.0.0.1:{TestSupport.GetFreeTcpPort()}";
                rewritten.Add(httpsFront with { BindAddress = env.HttpsBind });
            }

            // Any other transport rides along with its bind rewritten the same
            // way when it is host:port shaped (envelope, DNS, raw TCP); a pipe
            // name is kept as configured.
            foreach (var other in listeners.Where(
                l => l.Transport is not ("http" or "https")))
            {
                var bind = other.BindAddress.Contains(':', StringComparison.Ordinal)
                    ? $"127.0.0.1:{TestSupport.GetFreeTcpPort()}"
                    : other.BindAddress;
                rewritten.Add(other with { BindAddress = bind });
            }

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder.UseRodListeners(rewritten))
                .Build();
            await env.Host.StartAsync();

            // The operator API rides on the HTTP listener when one is configured;
            // otherwise dial it on the https listener (the TestServer-free path still
            // serves the operator endpoints over TLS, the test client trusts the CA).
            var baseAddress = env.HttpBind.Length > 0
                ? $"http://{env.HttpBind}"
                : $"https://{env.HttpsBind}";
            env.Http = MakeHttpClient(baseAddress, env.Host.Services);
            return env;
        }

        // Builds an HttpClient that trusts the dev CA, so it can dial either an HTTP
        // or HTTPS listener. For HTTP the TLS config is simply unused.
        private static HttpClient MakeHttpClient(string baseAddress, IServiceProvider services)
        {
            var handler = new SocketsHttpHandler();
            if (baseAddress.StartsWith("https://", StringComparison.Ordinal))
            {
                var ca = services.GetRequiredService<IImplantCertificateAuthority>().GetCaCertificate();
                handler.SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                    {
                        if (cert is null)
                            return false;
                        chain!.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                        chain.ChainPolicy.ExtraStore.Add(ca);
                        return chain.Build((X509Certificate2)cert);
                    },
                };
            }
            // Wrap in CookieHandler so the operator session persists across the
            // listener-routed requests.
            return new HttpClient(new CookieHandler(handler)) { BaseAddress = new Uri(baseAddress) };
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }
}
