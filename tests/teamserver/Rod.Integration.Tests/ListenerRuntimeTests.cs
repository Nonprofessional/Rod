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
/// </summary>
public class ListenerRuntimeTests
{
    [Fact]
    public async Task HttpListener_CreatedAtRuntime_ServesAndDeletes()
    {
        var extraPort = TestSupport.GetFreeTcpPort();
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "dev-http",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));
        await AuthenticatedHost.LoginAsync(env.Http);

        var created = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "runtime-http",
                Transport: "http",
                BindAddress: $"127.0.0.1:{extraPort}",
                PublicEndpoint: "http://runtime.example.test"));
        created.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        Assert.NotNull(listener);
        Assert.Equal("running", listener!.State);

        // The created listener is a real ingress: the app answers on its
        // socket (the anonymous health probe rides every listener).
        using (var probe = new HttpClient())
        {
            var health = await probe.GetAsync($"http://127.0.0.1:{extraPort}/health");
            health.EnsureSuccessStatusCode();
        }

        // And the roster reports it beside the startup listener.
        var roster = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        Assert.NotNull(roster);
        Assert.Equal(2, roster!.Length);
        Assert.Contains(roster, l => l.Name == "runtime-http" && l.State == "running");

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
    public async Task TcpListener_CreatedAtRuntime_AcceptsAndStops()
    {
        var port = TestSupport.GetFreeTcpPort();
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "dev-http",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));
        await AuthenticatedHost.LoginAsync(env.Http);

        var created = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "pivot-tcp",
                Transport: "tcp",
                BindAddress: $"127.0.0.1:{port}",
                PublicEndpoint: $"203.0.113.10:{port}"));
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
            Name: "dev-http",
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
            Name: "dev-http",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{taken}",
            PublicEndpoint: "http://localhost:5080"));
        await AuthenticatedHost.LoginAsync(env.Http);

        // The startup listener holds the port; a runtime create that asks for
        // the same socket is refused, not silently dropped.
        var created = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "collide",
                Transport: "http",
                BindAddress: $"127.0.0.1:{taken}",
                PublicEndpoint: "http://collide.example.test"));
        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);

        var roster = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        Assert.NotNull(roster);
        Assert.Single(roster!, l => l.Name == "dev-http");
    }

    [Fact]
    public async Task Create_MalformedRequest_IsRejected()
    {
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "dev-http",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost:5080"));
        await AuthenticatedHost.LoginAsync(env.Http);

        var badTransport = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "x", Transport: "carrier-pigeon", BindAddress: "127.0.0.1:9999", PublicEndpoint: "http://x.test"));
        Assert.Equal(HttpStatusCode.BadRequest, badTransport.StatusCode);

        var badBind = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "x", Transport: "http", BindAddress: "not an address", PublicEndpoint: "http://x.test"));
        Assert.Equal(HttpStatusCode.BadRequest, badBind.StatusCode);

        var badEndpoint = await env.Http.PostAsJsonAsync("/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "x", Transport: "http", BindAddress: "127.0.0.1:9999", PublicEndpoint: "guess"));
        Assert.Equal(HttpStatusCode.BadRequest, badEndpoint.StatusCode);
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
