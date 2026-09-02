using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: listeners are created and deleted at runtime through the
/// operator API, through the same real Kestrel sockets the startup
/// configuration binds. A created HTTP listener actually serves (the operator
/// API answers on its socket); a created stream (raw TCP) listener accepts
/// connections; deleting either unbinds its socket. A startup-configuration
/// listener is refused for deletion -- the configuration owns it -- and a
/// bind that collides with a live socket is refused, not silently lost.
/// A runtime listener belongs to one engagement, and enrollment through it
/// accepts only that engagement's tokens: a foreign token is refused whole
/// (unspent), while the shared startup tier stays token-scoped only.
/// </summary>
public class ListenerRuntimeTests
{
    [Fact]
    public async Task HttpListener_CreatedAtRuntime_ServesAndDeletes()
    {
        var extraPort = TestSupport.GetFreeTcpPort();
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "operator-http",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));
        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);

        var created = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "runtime-http",
                Transport: "http",
                BindAddress: $"127.0.0.1:{extraPort}",
                PublicEndpoint: "http://runtime.example.test",
                EngagementId: engagementId));
        created.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        Assert.NotNull(listener);
        Assert.Equal("running", listener!.State);
        Assert.Equal(engagementId, listener.EngagementId);

        // The created listener is a real ingress: the app answers on its
        // socket (the anonymous health probe rides every listener).
        using (var probe = new HttpClient())
        {
            var health = await probe.GetAsync($"http://127.0.0.1:{extraPort}/health");
            health.EnsureSuccessStatusCode();
        }

        // And the roster reports it beside the startup listener, the startup
        // one carrying no engagement (the shared tier).
        var roster = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        Assert.NotNull(roster);
        Assert.Equal(2, roster!.Length);
        Assert.Contains(roster, l => l.Name == "runtime-http" && l.EngagementId == engagementId);
        Assert.Contains(roster, l => l.Name == "operator-http" && l.EngagementId is null);

        // Delete: the roster drops it immediately, and the socket drains and
        // unbinds (Kestrel allows seconds for in-flight requests).
        var deleted = await env.Http.DeleteAsync($"/listeners/{listener.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var afterDelete = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        Assert.NotNull(afterDelete);
        Assert.DoesNotContain(afterDelete!, l => l.Id == listener.Id);

        await WaitForRefusedAsync(extraPort);
    }

    [Fact]
    public async Task ScopedListener_RefusesAForeignEngagementsToken()
    {
        var scopedPort = TestSupport.GetFreeTcpPort();
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "operator-http",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));
        await AuthenticatedHost.LoginAsync(env.Http);

        // Two engagements; the scoped listener belongs to the first.
        var owning = await CreateEngagementAsync(env.Http);
        var foreign = await CreateEngagementAsync(env.Http);
        var created = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "scoped-http",
                Transport: "http",
                BindAddress: $"127.0.0.1:{scopedPort}",
                PublicEndpoint: "http://scoped.example.test",
                EngagementId: owning));
        created.EnsureSuccessStatusCode();

        // The foreign engagement's token is refused on the scoped socket --
        // whole, before the redeem spends it.
        var foreignSecret = await MintTokenAsync(env.Http, foreign);
        using var scoped = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{scopedPort}") };
        var refused = await scoped.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: foreignSecret, Class: null));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // Unspent: the same token still enrolls on the shared tier, where the
        // token itself names the engagement.
        var enrolled = await env.Http.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: foreignSecret, Class: null));
        enrolled.EnsureSuccessStatusCode();

        // And the owning engagement's token enrolls through the scoped socket.
        var owningSecret = await MintTokenAsync(env.Http, owning);
        var scopedEnroll = await scoped.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: owningSecret, Class: null));
        scopedEnroll.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ANamedListener_SuppliesTheBuildEndpoint()
    {
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "operator-http",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));
        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);

        // The listener publishes the bare host:port redirector shape; a build
        // naming it dials that shape with the transport's scheme.
        var created = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "build-front",
                Transport: "http",
                BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
                PublicEndpoint: "203.0.113.10:8080",
                EngagementId: engagementId));
        created.EnsureSuccessStatusCode();
        var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();

        var built = await env.Http.PostAsJsonAsync($"/engagements/{engagementId}/payloads",
            new PayloadEndpoints.BuildPayloadRequest(
                Language: "DotNet",
                Class: "Stage2",
                TargetOs: "linux",
                TargetArch: "amd64",
                Endpoint: null,
                UriPath: "/beacon",
                SleepSeconds: 30,
                JitterSeconds: 10,
                KillDate: null,
                ListenerId: listener!.Id));
        Assert.Equal(HttpStatusCode.Created, built.StatusCode);

        // Another engagement's listener is refused: an artifact dials its own
        // engagement's ingress or nothing.
        var foreignEngagement = await CreateEngagementAsync(env.Http);
        var foreign = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "foreign-front",
                Transport: "http",
                BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
                PublicEndpoint: "203.0.113.11:8080",
                EngagementId: foreignEngagement));
        var foreignListener = await foreign.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        var wrongEngagement = await env.Http.PostAsJsonAsync($"/engagements/{engagementId}/payloads",
            new PayloadEndpoints.BuildPayloadRequest(
                Language: "DotNet", Class: "Stage2", TargetOs: "linux", TargetArch: "amd64",
                Endpoint: null, UriPath: "/beacon", SleepSeconds: 30, JitterSeconds: 10,
                KillDate: null, ListenerId: foreignListener!.Id));
        Assert.Equal(HttpStatusCode.BadRequest, wrongEngagement.StatusCode);

        // The shared tier (the operator front) is not implant ingress.
        var roster = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        var shared = Assert.Single(roster!, l => l.EngagementId is null);
        var sharedTier = await env.Http.PostAsJsonAsync($"/engagements/{engagementId}/payloads",
            new PayloadEndpoints.BuildPayloadRequest(
                Language: "DotNet", Class: "Stage2", TargetOs: "linux", TargetArch: "amd64",
                Endpoint: null, UriPath: "/beacon", SleepSeconds: 30, JitterSeconds: 10,
                KillDate: null, ListenerId: shared.Id));
        Assert.Equal(HttpStatusCode.BadRequest, sharedTier.StatusCode);

        // And a request naming both a listener and an endpoint is refused
        // rather than silently preferring one.
        var both = await env.Http.PostAsJsonAsync($"/engagements/{engagementId}/payloads",
            new PayloadEndpoints.BuildPayloadRequest(
                Language: "DotNet", Class: "Stage2", TargetOs: "linux", TargetArch: "amd64",
                Endpoint: "http://typed.example.test", UriPath: "/beacon", SleepSeconds: 30,
                JitterSeconds: 10, KillDate: null, ListenerId: listener.Id));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownEngagement_IsRejected()
    {
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "operator-http",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));
        await AuthenticatedHost.LoginAsync(env.Http);

        var created = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "orphan",
                Transport: "http",
                BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
                PublicEndpoint: "http://orphan.example.test",
                EngagementId: Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }

    [Fact]
    public async Task TcpListener_CreatedAtRuntime_AcceptsAndStops()
    {
        var port = TestSupport.GetFreeTcpPort();
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "operator-http",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));
        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);

        var created = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "pivot-tcp",
                Transport: "tcp",
                BindAddress: $"127.0.0.1:{port}",
                PublicEndpoint: $"203.0.113.10:{port}",
                EngagementId: engagementId));
        created.EnsureSuccessStatusCode();
        var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        Assert.NotNull(listener);

        // The stream service bound its socket: a TCP connection is accepted
        // (the check-in grammar answers; a bare connect succeeding is enough).
        using (var probe = new TcpClient())
        {
            await probe.ConnectAsync(IPAddress.Loopback, port);
            Assert.True(probe.Connected);
        }

        var deleted = await env.Http.DeleteAsync($"/listeners/{listener!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await WaitForRefusedAsync(port);
    }

    [Fact]
    public async Task Delete_StartupConfigurationListener_IsRefused()
    {
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "operator-http",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));
        await AuthenticatedHost.LoginAsync(env.Http);

        var roster = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        Assert.NotNull(roster);
        var startup = Assert.Single(roster!);

        var deleted = await env.Http.DeleteAsync($"/listeners/{startup.Id}");
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);

        // And the refusal left it serving.
        var after = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        Assert.NotNull(after);
        Assert.Single(after!, l => l.Id == startup.Id);
    }

    [Fact]
    public async Task Create_PortAlreadyInUse_IsRefused()
    {
        var taken = TestSupport.GetFreeTcpPort();
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "operator-http",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{taken}",
            PublicEndpoint: "http://localhost:5080"));
        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);

        // The startup listener holds the port; a runtime create that asks for
        // the same socket is refused, not silently dropped -- whatever the
        // engagement asking.
        var created = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "collide",
                Transport: "http",
                BindAddress: $"127.0.0.1:{taken}",
                PublicEndpoint: "http://collide.example.test",
                EngagementId: engagementId));
        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);

        var roster = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        Assert.NotNull(roster);
        Assert.Single(roster!, l => l.Name == "operator-http");
    }

    [Fact]
    public async Task Create_MalformedRequest_IsRejected()
    {
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "operator-http",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));
        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);

        var badTransport = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "x", Transport: "carrier-pigeon", BindAddress: "127.0.0.1:9999", PublicEndpoint: "http://x.test", EngagementId: engagementId));
        Assert.Equal(HttpStatusCode.BadRequest, badTransport.StatusCode);

        var badBind = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "x", Transport: "http", BindAddress: "not an address", PublicEndpoint: "http://x.test", EngagementId: engagementId));
        Assert.Equal(HttpStatusCode.BadRequest, badBind.StatusCode);

        var badEndpoint = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "x", Transport: "http", BindAddress: "127.0.0.1:9999", PublicEndpoint: "guess", EngagementId: engagementId));
        Assert.Equal(HttpStatusCode.BadRequest, badEndpoint.StatusCode);

        var noEngagement = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "x", Transport: "http", BindAddress: "127.0.0.1:9999", PublicEndpoint: "http://x.test", EngagementId: ""));
        Assert.Equal(HttpStatusCode.BadRequest, noEngagement.StatusCode);
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/engagements", new EngagementEndpoints.CreateEngagementRequest(Name: "listener runtime"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    private static async Task<string> MintTokenAsync(HttpClient client, string engagementId)
    {
        var response = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", null);
        response.EnsureSuccessStatusCode();
        var minted = await response.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
        return minted!.Secret;
    }

    // The port must stop accepting once the listener is gone; Kestrel allows
    // seconds to drain in-flight requests first, so poll rather than assert.
    private static async Task WaitForRefusedAsync(int port)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, CancellationToken.None);
            }
            catch (SocketException)
            {
                return; // refused: the socket is unbound.
            }
            await Task.Delay(250);
        }

        throw new Xunit.Sdk.XunitException($"Port {port} still accepts connections after the listener was deleted.");
    }

    /// <summary>
    /// A real Kestrel teamserver with one startup HTTP listener carrying the
    /// operator API, plus the authenticated operator session on it. The
    /// runtime-created listeners bind their own sockets beside it.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;

        public static async Task<TestEnv> StartAsync(ListenerConfig httpListener)
        {
            var env = new TestEnv();
            // Bound exactly as passed: tests allocate their free ports up
            // front (and some cases deliberately reuse one).
            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder.UseRodListeners(new[] { httpListener }))
                .Build();
            await env.Host.StartAsync();

            var handler = new SocketsHttpHandler();
            env.Http = new HttpClient(new CookieHandler(handler))
            {
                BaseAddress = new Uri($"http://{httpListener.BindAddress}"),
            };
            return env;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            if (Host is not null)
            {
                await Host.StopAsync();
                Host.Dispose();
            }
        }
    }
}
