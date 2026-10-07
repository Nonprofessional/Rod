using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Rod.CoreState;
using Rod.CoreState.Live;
using Rod.Operators.Webhooks;
using Rod.Persistence;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// The webhook durability posture (architecture.md Sec 4.4): the
/// subscription is durable -- it survives a teamserver restart with its
/// delivery bookkeeping, and the restored channel still pushes, until an
/// operator disables it from the API. Host A writes against a real
/// Postgres, is torn down, and host B -- a whole new process over the same
/// database -- carries the channel. Delivery itself stays best-effort, the
/// bus's own posture. Skips when Docker is absent, the same posture as the
/// core-state durability suite.
/// </summary>
[Collection("postgres")]
public sealed class WebhookDurabilityTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public WebhookDurabilityTests(PostgresFixture postgres)
        => _postgres = postgres;

    [Fact]
    public async Task ARegisteredChannel_SurvivesRestart_StillPushes_AndSilencesOnDisable()
    {
        Assert.True(_postgres.IsAvailable, "Postgres is not available; skipping.");

        string engagementId;
        using (var receiver = LoopbackWebhook.Start())
        {
            string subscriptionId;
            await using (var hostA = await TestEnv.StartAsync(_postgres))
            {
                (engagementId, subscriptionId) = await SetupChannelAsync(hostA.Http, receiver.Url);

                // One push under host A: the pump attaches on the tick, then
                // the event rides the bus.
                var busA = hostA.Host.Services.GetRequiredService<ILiveEventBus>();
                await hostA.Host.Services.GetRequiredService<WebhookDeliveryEngine>().TickOnceAsync();
                await PublishSessionOpenedAsync(busA, engagementId);
                await receiver.UntilAsync(1);

                var audit = await hostA.Http.GetFromJsonAsync<AuditListBody>(
                    $"/engagements/{engagementId}/audit?limit=100");
                Assert.Contains(audit!.Items, e =>
                    e.Kind == "WebhookDelivered" && e.Outcome.StartsWith("delivered:"));

                // Tear the process down: forwarder, bus, in-memory state.
                await hostA.DisposeAsync();
            }

            // Host B: a fresh teamserver over the same database. The channel
            // comes back with its bookkeeping and pushes again.
            await using var hostB = await TestEnv.StartAsync(_postgres);
            var restored = await hostB.Http.GetFromJsonAsync<SubscriptionBody>(
                $"/engagements/{engagementId}/webhook-subscriptions/{subscriptionId}");
            Assert.NotNull(restored);
            Assert.Equal(subscriptionId, restored!.Id);
            Assert.True(restored.Enabled);
            Assert.Equal(1, restored.DeliveryCount);

            var busB = hostB.Host.Services.GetRequiredService<ILiveEventBus>();
            await hostB.Host.Services.GetRequiredService<WebhookDeliveryEngine>().TickOnceAsync();
            await PublishSessionOpenedAsync(busB, engagementId);
            await receiver.UntilAsync(2);

            // The cancel, from the operator API: a disabled channel stays
            // quiet even when the matching event fires.
            var disable = await hostB.Http.PostAsync(
                $"/engagements/{engagementId}/webhook-subscriptions/{subscriptionId}:disable", content: null);
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
            await hostB.Host.Services.GetRequiredService<WebhookDeliveryEngine>().TickOnceAsync();
            await PublishSessionOpenedAsync(busB, engagementId);
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal(2, receiver.Requests.Count);
        }
    }

    private static async Task<(string EngagementId, string SubscriptionId)> SetupChannelAsync(
        HttpClient http, string url)
    {
        var engagement = await http.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Watchtower"));
        engagement.EnsureSuccessStatusCode();
        var engagementId = (await engagement.Content.ReadFromJsonAsync
            <EngagementEndpoints.EngagementResponse>())!.EngagementId;

        var register = await http.PostAsJsonAsync(
            $"/engagements/{engagementId}/webhook-subscriptions",
            new { Name = "overnight-watch", Url = url, EventKinds = new[] { "SessionOpened" } });
        register.EnsureSuccessStatusCode();
        var subscription = await register.Content.ReadFromJsonAsync<SubscriptionBody>();
        return (engagementId, subscription!.Id);
    }

    private static Task PublishSessionOpenedAsync(ILiveEventBus bus, string engagementId)
    {
        Assert.True(EngagementId.TryParse(engagementId, out var scope));
        return bus.PublishAsync(LiveEvent.SessionOpened(
            scope, OperatorId.New(), ImplantId.New(), "2.1", DateTimeOffset.UtcNow));
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

    private sealed class SubscriptionBody
    {
        public string Id { get; set; } = "";
        public bool Enabled { get; set; }
        public int DeliveryCount { get; set; }
    }

    private sealed class AuditListBody
    {
        public AuditEntry[] Items { get; set; } = [];
    }

    private sealed class AuditEntry
    {
        public string Kind { get; set; } = "";
        public string Outcome { get; set; } = "";
    }
}
