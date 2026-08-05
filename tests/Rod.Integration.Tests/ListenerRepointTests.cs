using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap M4.4 acceptance: swap a burned redirector without backend change.
/// A listener's public endpoint (the redirector implants dial) is decoupled from
/// its bind address (the socket Kestrel opens) -- repointing it at runtime moves
/// the address implants are told to dial and severs the old one, while the bound
/// socket keeps serving unchanged (architecture.md Sec 7/8). Drives a real
/// Kestrel teamserver whose listeners are bound via <c>UseRodListeners</c>.
/// </summary>
public class ListenerRepointTests
{
    [Fact]
    public async Task Repoint_SwapsPublicEndpoint_LeavesBindUntouched_AndSeversOldEndpoint()
    {
        const string oldEndpoint = "https://redirect-a.example.test";
        const string newEndpoint = "https://redirect-b.example.test";

        await using var env = await TestEnv.StartAsync(
            new ListenerConfig(
                Name: "operator-api",
                Transport: ListenerTransport.Http,
                BindAddress: $"127.0.0.1:{GetFreeTcpPort()}",
                PublicEndpoint: "http://op.example.test"),
            new ListenerConfig(
                Name: "mtls-redirected",
                Transport: ListenerTransport.Mtls,
                BindAddress: $"127.0.0.1:{GetFreeTcpPort()}",
                PublicEndpoint: oldEndpoint));

        var recordedBind = env.MtlsBind;
        var registry = env.Host.Services.GetRequiredService<IListenerRegistry>();

        // The old endpoint resolves to the listener before the repoint.
        var before = await registry.GetByPublicEndpointAsync(oldEndpoint);
        Assert.NotNull(before);
        Assert.Equal(recordedBind, before!.BindAddress);

        // Listeners are visible through the operator API; find the one to repoint.
        var list = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        Assert.NotNull(list);
        var target = Assert.Single(list!, l => l.Name == "mtls-redirected");
        Assert.Equal(oldEndpoint, target.PublicEndpoint);
        Assert.Null(target.RepointedAt);

        // Repoint the public endpoint to a fresh redirector.
        var response = await env.Http.PostAsJsonAsync(
            $"/listeners/{target.Id}:repoint",
            new ListenerEndpoints.RepointListenerRequest(PublicEndpoint: newEndpoint));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var repointed = await response.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        Assert.NotNull(repointed);
        Assert.Equal(newEndpoint, repointed!.PublicEndpoint);
        // The bind address is untouched -- the socket keeps serving. This is the
        // acceptance point: the redirector moved, the backend did not.
        Assert.Equal(recordedBind, repointed.BindAddress);
        Assert.NotNull(repointed.RepointedAt);

        // The new endpoint now resolves to the listener...
        var afterNew = await registry.GetByPublicEndpointAsync(newEndpoint);
        Assert.NotNull(afterNew);
        Assert.Equal(recordedBind, afterNew!.BindAddress);

        // ...and the old endpoint no longer resolves to anything -- the burned
        // redirector is severed. An implant still dialing it resolves to no
        // listener.
        Assert.Null(await registry.GetByPublicEndpointAsync(oldEndpoint));

        // The operator API reflects the repoint: same listener id, new endpoint,
        // bind unchanged.
        var reflected = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        Assert.NotNull(reflected);
        var row = Assert.Single(reflected!, l => l.Id == target.Id);
        Assert.Equal(newEndpoint, row.PublicEndpoint);
        Assert.Equal(recordedBind, row.BindAddress);
    }

    [Fact]
    public async Task Repoint_Returns404_ForUnknownListener()
    {
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "http-default",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost"));

        var response = await env.Http.PostAsJsonAsync(
            $"/listeners/{ListenerId.New()}:repoint",
            new ListenerEndpoints.RepointListenerRequest(PublicEndpoint: "https://new.example.test"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Repoint_Returns400_ForBlankEndpoint()
    {
        await using var env = await TestEnv.StartAsync(new ListenerConfig(
            Name: "http-default",
            Transport: ListenerTransport.Http,
            BindAddress: $"127.0.0.1:{GetFreeTcpPort()}",
            PublicEndpoint: "http://localhost"));

        var list = await env.Http.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
        var id = Assert.Single(list!).Id;

        var response = await env.Http.PostAsJsonAsync(
            $"/listeners/{id}:repoint",
            new ListenerEndpoints.RepointListenerRequest(PublicEndpoint: "   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// A real Kestrel teamserver whose listeners are bound via
    /// <see cref="TransportHost.UseRodListeners"/>, plus a plain-HTTP operator
    /// API. Mirrors the env in <see cref="ListenerTests"/>; kept local so this
    /// test is self-contained.
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

            env.Http = new HttpClient { BaseAddress = new Uri($"http://{env.HttpBind}") };
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
