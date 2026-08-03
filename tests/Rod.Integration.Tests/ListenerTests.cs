using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
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
/// Roadmap M2.2 acceptance: a listener accepts an implant connection end-to-end.
/// Drives the full slice through real Kestrel sockets configured via
/// <see cref="TransportHost.UseRodListeners"/> -- the listener abstraction (HTTP(S)
/// and mTLS) that fronts the same M1.x endpoints, with the bind address decoupled
/// from the public endpoint (architecture.md Sec 8). This is the listener-centric
/// counterpart to the <c>UseRodMtls</c>-based handshake tests: the connection
/// terminates through a named, registered listener rather than a bespoke socket.
/// </summary>
public class ListenerTests
{
    [Fact]
    public async Task HttpListener_AcceptsImplantConnection_EndToEnd()
    {
        // An HTTP listener serves the operator API and the implant enrollment
        // endpoint over plain HTTP (no client certificate). This is the end-to-end
        // M2.2 AC for the HTTP transport: an implant enrolls through the listener.
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "http-1",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{TestEnv.GetFreeTcpPort()}",
            PublicEndpoint: "http://c2.example.test"));

        // The listener is recorded with its bind address and public endpoint.
        var listeners = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        Assert.NotNull(listeners);
        var recorded = Assert.Single(listeners!);
        Assert.Equal("http-1", recorded.Name);
        Assert.Equal("http", recorded.Transport);
        Assert.Equal(env.HttpBind, recorded.BindAddress);
        Assert.Equal("http://c2.example.test", recorded.PublicEndpoint);
        Assert.Equal("running", recorded.State);

        // And the listener accepts an implant connection end-to-end: enroll against
        // the listener's bind address and receive the bound certificate.
        var secret = await MintTokenForNewEngagementAsync(env.Http);
        var response = await env.Http.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
        Assert.NotNull(enrolled);
        Assert.Equal(EnrollStatus.Ok, enrolled!.Status);
        Assert.False(string.IsNullOrWhiteSpace(enrolled.ImplantId));
    }

    [Fact]
    public async Task MtlsListener_AcceptsImplantConnection_EndToEnd()
    {
        // An mTLS listener terminates mutual TLS using the implant CA and carries
        // the beacon stream. This is the end-to-end M2.2 AC for the mTLS transport:
        // an implant connects through the listener, completes the handshake, and
        // appears online -- the same path the UseRodMtls handshake test drives, but
        // here through a named listener populated via UseRodListeners.
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "mtls-1",
            Transport: ListenerTransport.Mtls,
            BindAddress: $"127.0.0.1:{TestEnv.GetFreeTcpPort()}",
            PublicEndpoint: "https://c2.example.test"));

        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var sessions = env.Host.Services.GetRequiredService<ISessionRegistry>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();

        // Enroll an implant directly through the core ports, then present its leaf
        // over the listener's mTLS socket.
        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca);

        using var channel = env.ConnectBeacon(env.MtlsBind, leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, 1, 0));

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var response = ParseResponse(call.ResponseStream.Current);
        Assert.Equal(HandshakeStatus.Ok, response.Status);
        Assert.Equal(implant.EngagementId.ToString(), response.EngagementId);

        // The acceptance point: the implant connected through the listener and is
        // online in its engagement.
        var online = await sessions.ListActiveAsync(implant.EngagementId);
        Assert.Single(online, s => s.ImplantId == implant.Id);

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task Listener_BindAddress_IsDecoupled_FromPublicEndpoint()
    {
        // architecture.md Sec 8: the listener and the public endpoint are decoupled
        // so a burned redirector is replaceable without backend change. Here the
        // public endpoint is a redirector host that differs from the bind address;
        // the listener records both independently. The public endpoint is data, not
        // the socket -- swapping it (a different redirector) never touches the bind.
        // An HTTP listener rides alongside so the operator API is reachable without
        // an implant client certificate; the mTLS listener is the decoupling subject.
        await using var env = await TestEnv.StartAsync(
            new ListenerConfig(
                Name: "operator-api",
                Transport: ListenerTransport.Http,
                BindAddress: $"127.0.0.1:{TestEnv.GetFreeTcpPort()}",
                PublicEndpoint: "http://op.example.test"),
            new ListenerConfig(
                Name: "mtls-redirected",
                Transport: ListenerTransport.Mtls,
                BindAddress: $"127.0.0.1:{TestEnv.GetFreeTcpPort()}",
                PublicEndpoint: "https://redirect-a.example.test"));

        var recordedBind = env.MtlsBind;

        // The registry resolves the public endpoint back to the listener that
        // terminates it -- the lookup an implant dialing a redirector resolves to.
        var registry = env.Host.Services.GetRequiredService<IListenerRegistry>();
        var byPublic = await registry.GetByPublicEndpointAsync("https://redirect-a.example.test");
        Assert.NotNull(byPublic);
        Assert.Equal(recordedBind, byPublic!.BindAddress);

        // And it is visible, separately, through the operator API.
        var body = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        Assert.NotNull(body);
        var listener = Assert.Single(body!, l => l.Name == "mtls-redirected");
        Assert.Equal(recordedBind, listener.BindAddress);
        Assert.Equal("https://redirect-a.example.test", listener.PublicEndpoint);
        Assert.NotEqual(listener.BindAddress, listener.PublicEndpoint);
    }

    [Fact]
    public async Task GetListener_Returns404_ForUnknownId()
    {
        await using var env = await TestEnv.StartAsync(DefaultHttpListener());

        var response = await env.Http.GetAsync($"/listeners/{ListenerId.New()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static ListenerConfig DefaultHttpListener()
        => new("http-default", ListenerTransport.Http, $"127.0.0.1:{TestEnv.GetFreeTcpPort()}", "http://localhost");

    private static async Task<string> MintTokenForNewEngagementAsync(HttpClient client)
    {
        var createResponse = await client.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
            OwnerId: Guid.NewGuid(),
            OwnerHandle: "cneale",
            OwnerDisplayName: "Cecil Neale",
            Name: "Operation Smokeshow"));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(created);

        var mintResponse = await client.PostAsync($"/engagements/{created!.EngagementId}/stager-tokens", content: null);
        mintResponse.EnsureSuccessStatusCode();
        var token = await mintResponse.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
        Assert.NotNull(token);
        return token!.Secret;
    }

    private static async Task<(Implant Implant, X509Certificate2 Leaf, RSA LeafKey)> EnrollImplantAsync(
        IImplantRepository implants, IImplantCertificateAuthority ca)
    {
        var now = DateTimeOffset.UtcNow;
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(), "key-abc",
            now.AddDays(30), ImplantClass.Stage2, now);
        await implants.SaveAsync(implant);

        var leafKey = RSA.Create(2048);
        var issued = await ca.IssueWithKeyAsync(
            new ImplantCertificateSubject(implant.Id, implant.EngagementId), leafKey, CancellationToken.None);
        return (implant, X509CertificateLoader.LoadCertificate(issued.Leaf), leafKey);
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
        public string MtlsBind { get; private set; } = "";

        public static async Task<TestEnv> StartAsync(params ListenerConfig[] listeners)
        {
            var env = new TestEnv();

            // Pick free ports up front so the config's bind addresses match the
            // sockets Kestrel opens, and so tests can dial them.
            var httpListener = listeners.FirstOrDefault(l => l.Transport == ListenerTransport.Http);
            var mtlsListener = listeners.FirstOrDefault(l => l.Transport == ListenerTransport.Mtls);
            var rewritten = new List<ListenerConfig>();
            if (httpListener is not null)
            {
                env.HttpBind = $"127.0.0.1:{GetFreeTcpPort()}";
                rewritten.Add(httpListener with { BindAddress = env.HttpBind });
            }
            if (mtlsListener is not null)
            {
                env.MtlsBind = $"127.0.0.1:{GetFreeTcpPort()}";
                rewritten.Add(mtlsListener with { BindAddress = env.MtlsBind });
            }

            env.Host = TransportHost.CreateHostBuilder()
                .ConfigureWebHost(webBuilder => webBuilder.UseRodListeners(rewritten))
                .Build();
            await env.Host.StartAsync();

            // The operator API rides on the HTTP listener when one is configured;
            // otherwise dial it on the mTLS listener (the TestServer-free path still
            // serves the operator endpoints over TLS, the test client trusts the CA).
            var baseAddress = env.HttpBind.Length > 0
                ? $"http://{env.HttpBind}"
                : $"https://{env.MtlsBind}";
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
            return new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        }

        // Connects a gRPC channel that performs the client side of mTLS against the
        // given listener bind address: presents the implant leaf (with its private
        // key) and trusts the dev CA as the server identity.
        public GrpcChannel ConnectBeacon(string bindAddress, X509Certificate2 leaf, RSA leafKey)
        {
            var leafWithKey = leaf.HasPrivateKey ? leaf : leaf.CopyWithPrivateKey(leafKey);
            var ca = Host.Services.GetRequiredService<IImplantCertificateAuthority>().GetCaCertificate();

            var handler = new SocketsHttpHandler();
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { leafWithKey },
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

            return GrpcChannel.ForAddress($"https://{bindAddress}", new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = true,
            });
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }

        public static int GetFreeTcpPort()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
