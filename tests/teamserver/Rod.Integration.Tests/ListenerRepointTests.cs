using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: swap a burned redirector without backend change. An engagement
/// listener's public endpoint (the redirector implants dial) is decoupled from
/// its bind address (the socket Kestrel opens) -- repointing it at runtime
/// moves the address implants are told to dial and severs the old one, while
/// the bound socket keeps serving unchanged (architecture.md Sec 7/8). Drives
/// a real Kestrel teamserver; the repointed listener is created through the
/// engagement-scoped operator API, the shape every implant-facing listener
/// takes.
/// </summary>
public class ListenerRepointTests
{
    [Fact]
    public async Task Repoint_SwapsPublicEndpoint_LeavesBindUntouched_AndSeversOldEndpoint()
    {
        const string oldEndpoint = "https://redirect-a.example.test";
        const string newEndpoint = "https://redirect-b.example.test";

        await using var env = await TestEnv.StartAsync();
        var engagementId = await CreateEngagementAsync(env.Http);
        var listener = await CreateListenerAsync(env.Http, engagementId, "tls-redirected", "https", oldEndpoint);
        var recordedBind = listener.BindAddress;

        // Repoint the public endpoint to a fresh redirector.
        var response = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/listeners/{listener.Id}:repoint",
            new ListenerEndpoints.RepointListenerRequest(PublicEndpoint: newEndpoint));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var repointed = await response.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        Assert.NotNull(repointed);
        Assert.Equal(newEndpoint, repointed!.PublicEndpoint);
        // The bind address is untouched -- the socket keeps serving. This is the
        // acceptance point: the redirector moved, the backend did not.
        Assert.Equal(recordedBind, repointed.BindAddress);
        Assert.NotNull(repointed.RepointedAt);

        // The engagement's listing reflects the repoint: same listener id, new
        // endpoint, bind unchanged.
        var reflected = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>(
            $"/engagements/{engagementId}/listeners");
        Assert.NotNull(reflected);
        var row = Assert.Single(reflected!, l => l.Id == listener.Id);
        Assert.Equal(newEndpoint, row.PublicEndpoint);
        Assert.Equal(recordedBind, row.BindAddress);
    }

    [Fact]
    public async Task Repoint_Returns404_ForUnknownListener()
    {
        await using var env = await TestEnv.StartAsync();
        var engagementId = await CreateEngagementAsync(env.Http);

        var response = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/listeners/{ListenerId.New()}:repoint",
            new ListenerEndpoints.RepointListenerRequest(PublicEndpoint: "https://new.example.test"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Repoint_RefusesAnEndpointThatIsNeitherUrlNorHostPort()
    {
        await using var env = await TestEnv.StartAsync();
        var engagementId = await CreateEngagementAsync(env.Http);
        var listener = await CreateListenerAsync(env.Http, engagementId, "front", "http", "http://old.example.test");

        // A public endpoint is the address baked payloads dial: "666" is
        // neither an absolute http(s) URL nor a host:port pair, and accepting
        // it would strand every payload built against the listener. The
        // repoint is refused naming the accepted shapes instead.
        var garbage = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/listeners/{listener.Id}:repoint",
            new ListenerEndpoints.RepointListenerRequest(PublicEndpoint: "666"));
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);

        // The bare host:port redirector shape is the documented deployment
        // form (redirectors.md) and is accepted.
        var front = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/listeners/{listener.Id}:repoint",
            new ListenerEndpoints.RepointListenerRequest(PublicEndpoint: "203.0.113.10:443"));
        Assert.Equal(HttpStatusCode.OK, front.StatusCode);
    }

    [Fact]
    public async Task Repoint_Returns400_ForBlankEndpoint()
    {
        await using var env = await TestEnv.StartAsync();
        var engagementId = await CreateEngagementAsync(env.Http);
        var listener = await CreateListenerAsync(env.Http, engagementId, "front", "http", "http://old.example.test");

        var response = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/listeners/{listener.Id}:repoint",
            new ListenerEndpoints.RepointListenerRequest(PublicEndpoint: "   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/engagements", new EngagementEndpoints.CreateEngagementRequest(Name: "repoint walk"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
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

    /// <summary>
    /// A real Kestrel teamserver with the loopback operator front, plus the
    /// authenticated operator session on it. The engagement's listeners are
    /// created through the scoped API and bind their own sockets beside it.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            var httpBind = $"127.0.0.1:{TestSupport.GetFreeTcpPort()}";
            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder.UseRodListeners(new[]
                {
                    new ListenerConfig(
                        Name: "operator-http",
                        Transport: "http",
                        BindAddress: httpBind,
                        PublicEndpoint: "http://localhost:5080"),
                }))
                .Build();
            await env.Host.StartAsync();

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://{httpBind}"),
            };
            await AuthenticatedHost.LoginAsync(env.Http);
            return env;
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
