using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Sessions;
using Rod.CoreState.Staging;
using Rod.CoreState.Tasks;
using Rod.Persistence;
using Rod.Transport;
using Rod.Transport.Endpoints;
// The domain entity shares its name with System.Threading.Tasks.Task; the tests
// use the BCL type for async method signatures, so pin it here. The domain Task
// is reached by its full name where the helper builds one.
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap M10.1 durability: core state survives a teamserver restart when the
/// Postgres-backed adapters are wired. Each test covers the stores delivered so
/// far -- operators, engagements, implants, sessions, tasks, and stager tokens
/// -- over a live PostgreSQL container. The full acceptance test (every
/// aggregate plus the audit chain) lands once the remaining adapters ship.
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

    [Fact]
    public async Task ImplantsAndSessions_SurviveRestart_WhenPostgresWired()
    {
        if (!_postgres.IsAvailable)
        {
            // No Docker in this environment; skip, not fail.
            return;
        }

        var connectionString = _postgres.ConnectionString;

        // --- Host A: apply the schema, then create an engagement (needed to scope
        //     the implants), a top-level implant, a child implant derived from it,
        //     and a session that connects, reconnects (closing the prior), and is
        //     finally left with the child online and the parent retired. ---
        EngagementId engagementId;
        ImplantId parentId;
        ImplantId childId;
        SessionId closedSessionId;
        SessionId activeSessionId;

        await using (var envA = await TestEnv.StartAsync(connectionString))
        {
            await EnsureSchemaAsync(envA.Host);

            // A real engagement through the HTTP API so the engagement scoping is
            // exercised end to end (the membership it creates is irrelevant here).
            var ownerId = OperatorId.New();
            var created = await envA.Http.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
                OwnerId: ownerId.Value,
                OwnerHandle: "cneale",
                OwnerDisplayName: "Cecil Neale",
                Name: "Operation Smokeshow"));
            created.EnsureSuccessStatusCode();
            var engagement = await created.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
            Assert.True(EngagementId.TryParse(engagement!.EngagementId, out engagementId));

            var implants = envA.Host.Services.GetRequiredService<IImplantRepository>();
            var sessions = envA.Host.Services.GetRequiredService<ISessionRegistry>();

            // Enroll a top-level implant and a child derived from it (architecture
            // Sec 5.2). Both land in the same engagement; the child carries the
            // parent's id.
            var parent = Implant.Enroll(
                ImplantId.New(),
                engagementId,
                key: "k_parent",
                killDate: DateTimeOffset.UtcNow.AddHours(1),
                @class: ImplantClass.Stage2,
                createdAt: DateTimeOffset.UtcNow,
                deployedBy: ownerId);
            parentId = parent.Id;
            await implants.SaveAsync(parent);

            var child = Implant.EnrollChild(
                ImplantId.New(),
                engagementId,
                key: "k_child",
                killDate: parent.KillDate,
                @class: ImplantClass.Pivot,
                createdAt: DateTimeOffset.UtcNow,
                deployedBy: ownerId,
                parentImplantId: parentId);
            childId = child.Id;
            await implants.SaveAsync(child);

            // Open a session on the parent, then open another on the same parent
            // (a reconnect): the registry must close the prior active session
            // before opening the new one (the "at most one active session" rule).
            // The first session's id is captured so we can assert on host B that it
            // came back Closed with an EndedAt, and still appears in history.
            var first = await sessions.OpenAsync(parent, capabilities: new[] { "shell.exec" }, at: DateTimeOffset.UtcNow);
            closedSessionId = first.Id;
            var reconnect = await sessions.OpenAsync(parent, capabilities: new[] { "shell.exec", "probe.read" }, at: DateTimeOffset.UtcNow);
            // Then the reconnect's stream ends: an explicit close leaves the parent
            // with no active session so the child's session is the only live one.
            await sessions.CloseAsync(reconnect.Id, at: DateTimeOffset.UtcNow);

            // Open a session on the child and leave it active -- the online implant
            // after restart. Then retire the parent (M4.4): the entity records
            // RetiredAt; the session is left to history.
            activeSessionId = (await sessions.OpenAsync(child, capabilities: new[] { "tunnel.open" }, at: DateTimeOffset.UtcNow)).Id;
            var storedParent = await implants.GetOrThrowAsync(parentId);
            Assert.True(storedParent.Retire(DateTimeOffset.UtcNow));
            await implants.SaveAsync(storedParent);
        }

        // --- Host B: fresh teamserver, same Postgres. The durable adapters read
        //     both implants and all sessions back. ---
        await using var envB = await TestEnv.StartAsync(connectionString);

        var implantsB = envB.Host.Services.GetRequiredService<IImplantRepository>();
        var sessionsB = envB.Host.Services.GetRequiredService<ISessionRegistry>();

        // The top-level implant round-trips, with its parentage null and its
        // retirement recorded.
        var parentRow = await implantsB.FindAsync(parentId);
        Assert.NotNull(parentRow);
        Assert.Null(parentRow!.ParentImplantId);
        Assert.True(parentRow.IsRetired);
        Assert.NotNull(parentRow.RetiredAt);

        // The child implant round-trips with its parent linkage and is not retired.
        var childRow = await implantsB.FindAsync(childId);
        Assert.NotNull(childRow);
        Assert.Equal(parentId, childRow!.ParentImplantId);
        Assert.False(childRow.IsRetired);

        // ListByEngagement returns both, oldest first (parent created before
        // child).
        var byEngagement = await implantsB.ListByEngagementAsync(engagementId);
        Assert.Equal(2, byEngagement.Count);
        Assert.Equal(parentId, byEngagement[0].Id);
        Assert.Equal(childId, byEngagement[1].Id);

        // The first session was closed by the reconnect: it reads as Closed with
        // an EndedAt, and still appears in the parent's history.
        var closedRow = await sessionsB.FindAsync(closedSessionId);
        Assert.NotNull(closedRow);
        Assert.Equal(SessionStatus.Closed, closedRow!.Status);
        Assert.NotNull(closedRow.EndedAt);

        // The child's session is the one active session for the child (and the
        // one active session in the engagement), exactly as presence reads it.
        var activeRow = await sessionsB.FindAsync(activeSessionId);
        Assert.NotNull(activeRow);
        Assert.Equal(SessionStatus.Active, activeRow!.Status);
        Assert.Null(activeRow.EndedAt);

        var activeForChild = await sessionsB.GetActiveAsync(childId);
        Assert.NotNull(activeForChild);
        Assert.Equal(activeSessionId, activeForChild!.Id);

        var activeInEngagement = await sessionsB.ListActiveAsync(engagementId);
        var active = Assert.Single(activeInEngagement);
        Assert.Equal(activeSessionId, active.Id);

        // The parent flapped twice (connect, reconnect) so its history holds both
        // sessions, oldest first.
        var parentHistory = await sessionsB.ListByImplantAsync(parentId);
        Assert.Equal(2, parentHistory.Count);
        Assert.Equal(closedSessionId, parentHistory[0].Id);
    }

    [Fact]
    public async Task Tasks_SurviveRestart_AndDispatchInFifoOrder_WhenPostgresWired()
    {
        if (!_postgres.IsAvailable)
        {
            // No Docker in this environment; skip, not fail.
            return;
        }

        var connectionString = _postgres.ConnectionString;

        // --- Host A: apply the schema, create an engagement + implant, then
        //     enqueue three tasks on the implant and dispatch the first. The FIFO
        //     order is what the enqueue_seq column must preserve across restart. ---
        EngagementId engagementId;
        ImplantId implantId;
        TaskId firstId;
        TaskId secondId;
        TaskId thirdId;

        await using (var envA = await TestEnv.StartAsync(connectionString))
        {
            await EnsureSchemaAsync(envA.Host);

            var ownerId = OperatorId.New();
            var created = await envA.Http.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
                OwnerId: ownerId.Value,
                OwnerHandle: "cneale",
                OwnerDisplayName: "Cecil Neale",
                Name: "Operation Smokeshow"));
            created.EnsureSuccessStatusCode();
            var engagement = await created.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
            Assert.True(EngagementId.TryParse(engagement!.EngagementId, out engagementId));

            var implants = envA.Host.Services.GetRequiredService<IImplantRepository>();
            var tasks = envA.Host.Services.GetRequiredService<ITaskRepository>();

            var implant = Implant.Enroll(
                ImplantId.New(),
                engagementId,
                key: "k_fifo",
                killDate: DateTimeOffset.UtcNow.AddHours(1),
                @class: ImplantClass.Stage2,
                createdAt: DateTimeOffset.UtcNow,
                deployedBy: ownerId);
            implantId = implant.Id;
            await implants.SaveAsync(implant);

            firstId = await EnqueueAsync(tasks, engagementId, implantId, ownerId, "shell.exec", "echo one");
            secondId = await EnqueueAsync(tasks, engagementId, implantId, ownerId, "shell.exec", "echo two");
            thirdId = await EnqueueAsync(tasks, engagementId, implantId, ownerId, "shell.exec", "echo three");

            // Dispatch the first so the restart also carries a non-Queued status.
            var first = await tasks.NextPendingAsync(implantId);
            Assert.NotNull(first);
            Assert.Equal(firstId, first!.Id);
            first.MarkDispatched(DateTimeOffset.UtcNow);
            await tasks.SaveAsync(first);
        }

        // --- Host B: fresh teamserver, same Postgres. The durable task store
        //     reads all three back; the dispatched one kept its status, and the
        //     remaining two dequeue in enqueue order (FIFO via enqueue_seq). ---
        await using var envB = await TestEnv.StartAsync(connectionString);

        var tasksB = envB.Host.Services.GetRequiredService<ITaskRepository>();

        // The dispatched first task survived with its Dispatched status (a re-save
        // updated the lifecycle columns without touching enqueue_seq).
        var firstRow = await tasksB.FindAsync(firstId);
        Assert.NotNull(firstRow);
        Assert.Equal(Rod.CoreState.Tasks.TaskStatus.Dispatched, firstRow!.Status);
        Assert.NotNull(firstRow.DispatchedAt);

        // NextPending skips the dispatched one and returns the second (the oldest
        // still-Queued), proving FIFO order survived the restart.
        var next = await tasksB.NextPendingAsync(implantId);
        Assert.NotNull(next);
        Assert.Equal(secondId, next!.Id);

        // The implant and engagement histories read in enqueue order, with the
        // dispatched first task in its enqueue position (not moved to the back).
        var byImplant = await tasksB.ListByImplantAsync(implantId);
        Assert.Equal(3, byImplant.Count);
        Assert.Equal(firstId, byImplant[0].Id);
        Assert.Equal(secondId, byImplant[1].Id);
        Assert.Equal(thirdId, byImplant[2].Id);

        var byEngagement = await tasksB.ListByEngagementAsync(engagementId);
        Assert.Equal(3, byEngagement.Count);
        Assert.Equal(firstId, byEngagement[0].Id);

        // Complete the second task and confirm the result columns round-trip.
        var second = await tasksB.FindAsync(secondId);
        Assert.NotNull(second);
        // The second was still Queued on host B (NextPending does not consume);
        // dispatch then complete it so the lifecycle advances across a save.
        second!.MarkDispatched(DateTimeOffset.UtcNow);
        second.Complete(output: "two", outcome: TaskOutcome.Succeeded, at: DateTimeOffset.UtcNow);
        await tasksB.SaveAsync(second);

        var secondCompleted = await tasksB.FindAsync(secondId);
        Assert.NotNull(secondCompleted);
        Assert.Equal(Rod.CoreState.Tasks.TaskStatus.Completed, secondCompleted!.Status);
        Assert.Equal("two", secondCompleted.Output);
        Assert.Equal(TaskOutcome.Succeeded, secondCompleted.Outcome);
    }

    [Fact]
    public async Task StagerTokens_SurviveRestart_AndRedeemAtomically_WhenPostgresWired()
    {
        if (!_postgres.IsAvailable)
        {
            // No Docker in this environment; skip, not fail.
            return;
        }

        var connectionString = _postgres.ConnectionString;

        // --- Host A: apply the schema, create an engagement, mint a single-use
        //     token. The plaintext secret is captured now; only its hash is in
        //     Postgres. ---
        string secret;
        EngagementId engagementId;
        OperatorId ownerId;

        await using (var envA = await TestEnv.StartAsync(connectionString))
        {
            await EnsureSchemaAsync(envA.Host);

            ownerId = OperatorId.New();
            var created = await envA.Http.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
                OwnerId: ownerId.Value,
                OwnerHandle: "cneale",
                OwnerDisplayName: "Cecil Neale",
                Name: "Operation Smokeshow"));
            created.EnsureSuccessStatusCode();
            var engagement = await created.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
            Assert.True(EngagementId.TryParse(engagement!.EngagementId, out engagementId));

            var tokens = envA.Host.Services.GetRequiredService<IStagerTokenService>();
            var minted = await tokens.MintAsync(engagementId, ownerId, DateTimeOffset.UtcNow);
            secret = minted.Secret;
        }

        // --- Host B: fresh teamserver, same Postgres. The hash round-trips, so a
        //     redeem with the captured plaintext succeeds and attributes back to
        //     the minting operator and engagement. A second redeem of the now-spent
        //     single-use token refuses as Spent (the durable store keeps the row at
        //     zero rather than deleting it). ---
        await using var envB = await TestEnv.StartAsync(connectionString);

        var tokensB = envB.Host.Services.GetRequiredService<IStagerTokenService>();

        var redeemed = await tokensB.RedeemAsync(secret, DateTimeOffset.UtcNow);
        Assert.Equal(engagementId, redeemed.EngagementId);
        Assert.Equal(ownerId, redeemed.IssuedBy);

        var second = await Assert.ThrowsAsync<StagerTokenRedeemException>(
            () => tokensB.RedeemAsync(secret, DateTimeOffset.UtcNow));
        Assert.Equal(StagerTokenRedeemReason.Spent, second.Reason);

        // An unknown secret refuses as Unknown, and a malformed one never reaches
        // the lookup (also Unknown).
        var unknown = await Assert.ThrowsAsync<StagerTokenRedeemException>(
            () => tokensB.RedeemAsync("not-a-real-secret", DateTimeOffset.UtcNow));
        Assert.Equal(StagerTokenRedeemReason.Unknown, unknown.Reason);

        var malformed = await Assert.ThrowsAsync<StagerTokenRedeemException>(
            () => tokensB.RedeemAsync("???!", DateTimeOffset.UtcNow));
        Assert.Equal(StagerTokenRedeemReason.Unknown, malformed.Reason);
    }

    // Helper: enqueue a task and return its id, keeping the test bodies linear.
    private static async Task<TaskId> EnqueueAsync(
        ITaskRepository tasks,
        EngagementId engagementId,
        ImplantId implantId,
        OperatorId issuedBy,
        string verb,
        string arguments)
    {
        var task = Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), engagementId, implantId, issuedBy, verb, arguments, DateTimeOffset.UtcNow);
        await tasks.SaveAsync(task);
        return task.Id;
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
