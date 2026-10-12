using System.Linq;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Rod.CoreState;
using Rod.CoreState.Listeners;
using Rod.CoreState.Redirectors;
using Rod.Persistence;
using Rod.Transport;

namespace Rod.Integration.Tests;

/// <summary>
/// The redirector fleet's durability (architecture.md Sec 8): a node
/// enrolled against one teamserver process reconnects to its restart with
/// the minted node secret, and a listener's front list survives the same
/// restart so the pushed rules are unchanged.
/// </summary>
[Collection("postgres")]
public sealed class RedirectorFleetDurabilityTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public RedirectorFleetDurabilityTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task FleetAndFronts_SurviveRestart_WhenPostgresWired()
    {
        if (!_postgres.IsAvailable)
            return; // no Docker on this machine; durability is Postgres-only

        RedirectorNodeId nodeId;
        string nodeSecret;
        Guid listenerId;
        await using (var hostA = await TestEnv.StartAsync(_postgres))
        {
            // Mint through the operator API, then enroll through the control
            // channel like a deployed node would.
            var minted = await ReadJsonAsync(
                await hostA.Http.PostAsJsonAsync("/redirectors/credentials", new { name = "edge-fra-1" }));
            using (var socket = await ControlSocketAsync(hostA.Http.BaseAddress!))
            {
                var enrolled = await ControlExchangeAsync(
                    socket,
                    new { type = "enroll", credential = minted.GetProperty("secret").GetString(), version = "1.0.0", commit = "abc123" });
                nodeId = RedirectorNodeId.TryParse(enrolled.GetProperty("nodeId").GetString(), out var parsed)
                    ? parsed
                    : throw new InvalidOperationException("The enrolled response carried no node id.");
                nodeSecret = enrolled.GetProperty("nodeSecret").GetString()!;
            }

            // A front on an engagement listener, persisted with its
            // definition.
            listenerId = Guid.NewGuid();
            await hostA.Host.Services.GetRequiredService<IListenerStore>().SaveAsync(
                new ListenerDefinition(
                    listenerId,
                    new EngagementId(Guid.NewGuid()),
                    "https-ingress",
                    "https",
                    "10.0.0.5:8443",
                    "https://c2.example.test:443",
                    DateTimeOffset.UtcNow,
                    Fronts: new List<ListenerFront>
                    {
                        new(Guid.NewGuid(), nodeId, "0.0.0.0:443", "https://c2.example.test:443", null, DateTimeOffset.UtcNow),
                    }));
        }

        await using var hostB = await TestEnv.StartAsync(_postgres);

        // The node reconnects with the secret its enrollment minted, and the
        // welcome carries the front that survived the restart.
        using (var socket = await ControlSocketAsync(hostB.Http.BaseAddress!))
        {
            var welcome = await ControlExchangeAsync(
                socket, new { type = "hello", nodeId = nodeId.ToString(), nodeSecret });
            Assert.Equal("welcome", welcome.GetProperty("type").GetString());
            var rule = welcome.GetProperty("rules").GetProperty("rules").EnumerateArray().Single();
            Assert.Equal(443, rule.GetProperty("listenPort").GetInt32());
            Assert.Equal("10.0.0.5", rule.GetProperty("upstreamHost").GetString());
            Assert.Equal(8443, rule.GetProperty("upstreamPort").GetInt32());
        }

        var roster = await ReadJsonAsync(await hostB.Http.GetAsync("/redirectors"));
        var row = roster.EnumerateArray().Single(n => n.GetProperty("id").GetString() == nodeId.ToString());
        Assert.Equal("edge-fra-1", row.GetProperty("name").GetString());
        Assert.Equal("1.0.0", row.GetProperty("reportedVersion").GetString());

        var definition = await hostB.Host.Services.GetRequiredService<IListenerStore>().FindAsync(listenerId);
        Assert.NotNull(definition);
        var front = Assert.Single(definition!.FrontList);
        Assert.Equal(nodeId, front.NodeId);
        Assert.Equal("0.0.0.0:443", front.ListenEndpoint);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static async Task<WebSocket> ControlSocketAsync(Uri baseAddress)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://{baseAddress.Host}:{baseAddress.Port}/redirectors/control"),
            CancellationToken.None);
        return socket;
    }

    private static async Task<JsonElement> ControlExchangeAsync(WebSocket socket, object message)
    {
        var send = JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web);
        await socket.SendAsync(send, WebSocketMessageType.Text, true, CancellationToken.None);

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var received = await socket.ReceiveAsync(chunk, CancellationToken.None);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("The socket closed before a message arrived.");
            buffer.Write(chunk, 0, received.Count);
            if (received.EndOfMessage)
                return JsonSerializer.Deserialize<JsonElement>(buffer.ToArray());
        }
    }

    // The Postgres-backed host pair the core-state durability suite uses:
    // schema applied before start, proxy-bypassing client, cookie session.
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;

        public static async Task<TestEnv> StartAsync(PostgresFixture postgres)
        {
            var env = new TestEnv();
            var httpPort = TestSupport.GetFreeTcpPort();
            var config = AuthenticatedHost.BuildConfig(
                extend: dict => dict["ConnectionStrings:Postgres"] = postgres.ConnectionString);

            env.Host = TransportHost.CreateHostBuilder(
                    configuration: config,
                    configureServices: services => AuthenticatedHost.ComposeServices(
                        services, config, extra: s => s.AddRodPersistence(config)),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints))
                .ConfigureWebHost(webBuilder => webBuilder
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(httpPort)))
                .Build();

            // The host does not auto-migrate; the test creates the schema the
            // way an operator would, with one bounded retry for a container
            // that loses its first fresh connection.
            try
            {
                await EnsureSchemaAsync(env.Host);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                await EnsureSchemaAsync(env.Host);
            }
            await env.Host.StartAsync();

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler { UseProxy = false }))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{httpPort}"),
            };
            await AuthenticatedHost.LoginAsync(env.Http);
            return env;
        }

        private static async Task EnsureSchemaAsync(IHost host)
        {
            var factory = host.Services.GetRequiredService<IDbContextFactory<RodPersistenceDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
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
