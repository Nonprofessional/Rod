using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.Persistence;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap M10.1 durability: core state survives a teamserver restart when the
/// Postgres-backed adapters are wired. This is the partial first increment --
/// operators and engagements (the two stores delivered so far) -- over a live
/// PostgreSQL container. The full acceptance test (every aggregate plus the
/// audit chain) lands once the remaining adapters ship.
/// </summary>
/// <remarks>
/// Mirrors <see cref="AuditRetentionTests"/>: host A writes against a real
/// Postgres, is torn down (its process, listeners, and in-memory state vanish),
/// and host B starts over the same database. What the durable adapters persisted
/// must come back whole. The test skips (not fails) when Docker is absent.
/// </remarks>
public sealed class CoreStateDurabilityTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public CoreStateDurabilityTests(PostgresFixture postgres)
        => _postgres = postgres;

    [Fact]
    public async Task OperatorsAndEngagements_SurviveRestart_WhenPostgresWired()
    {
        if (!_postgres.IsAvailable)
        {
            // No Docker in this environment; skip, not fail. The rest of the
            // suite stays green.
            return;
        }

        var connectionString = _postgres.ConnectionString;

        // --- Host A: apply the schema, then create an operator (implicitly, as
        //     the engagement owner) and an engagement through the HTTP API. Both
        //     land in Postgres. ---
        OperatorId operatorId;
        EngagementId engagementId;

        await using (var envA = await TestEnv.StartAsync(connectionString))
        {
            await EnsureSchemaAsync(envA.Host);

            operatorId = OperatorId.New();
            var created = await envA.Http.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
                OwnerId: operatorId.Value,
                OwnerHandle: "cneale",
                OwnerDisplayName: "Cecil Neale",
                Name: "Operation Smokeshow"));
            created.EnsureSuccessStatusCode();
            var engagement = await created.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
            Assert.True(EngagementId.TryParse(engagement!.EngagementId, out engagementId));
        }
        // Host A is disposed: the durable adapters' DbContexts are gone, but the
        // rows they wrote remain in Postgres.

        // --- Host B: a fresh teamserver over the same Postgres. Its in-memory
        //     adapters are empty, but the durable adapters read the rows back. ---
        await using var envB = await TestEnv.StartAsync(connectionString);

        var operators = envB.Host.Services.GetRequiredService<IOperatorRepository>();
        var engagements = envB.Host.Services.GetRequiredService<IEngagementRepository>();

        // The operator created on host A is present on host B -- the typed id
        // round-trips through the uuid column.
        var operatorRow = await operators.FindAsync(operatorId);
        Assert.NotNull(operatorRow);
        Assert.Equal("cneale", operatorRow!.Handle);
        Assert.Equal("Cecil Neale", operatorRow.DisplayName);

        // The engagement is present with its owner membership intact (the owned
        // collection survived the reload).
        var engagementRow = await engagements.FindAsync(engagementId);
        Assert.NotNull(engagementRow);
        Assert.Equal("Operation Smokeshow", engagementRow!.Name);
        Assert.Equal(operatorId, engagementRow.OwnerId);
        Assert.Single(engagementRow.Members);
        Assert.Equal(operatorId, engagementRow.Members[0].OperatorId);
    }

    // Applies the InitialCreate migration to the container's database. The host
    // itself does not auto-migrate (migrations are a deliberate operator step),
    // so the test creates the schema the durable adapters expect.
    private static async Task EnsureSchemaAsync(IHost host)
    {
        var factory = host.Services.GetRequiredService<IDbContextFactory<RodPersistenceDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;

        public static async Task<TestEnv> StartAsync(string connectionString)
        {
            var env = new TestEnv();
            var httpPort = GetFreeTcpPort();

            // ConnectionStrings:Postgres selects the durable adapters (the same
            // opt-in shape as Audit:DataDirectory). In-memory config mirrors what
            // appsettings.json supplies for the real host.
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = connectionString,
                })
                .Build();

            env.Host = TransportHost.CreateHostBuilder(
                    configuration: config,
                    configureServices: services => services.AddRodPersistence(config))
                .ConfigureWebHost(webBuilder => webBuilder
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(httpPort)))
                .Build();
            await env.Host.StartAsync();

            env.Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}") };
            return env;
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }

        private static int GetFreeTcpPort()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
