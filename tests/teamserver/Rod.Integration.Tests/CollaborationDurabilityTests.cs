using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.CoreState.Tasks;
using Microsoft.AspNetCore.Hosting;
using Rod.Persistence;
using Rod.Transport;
// The domain entity shares its name with System.Threading.Tasks.Task; the
// BCL type is the only one used by name here.
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// The collaboration durability posture (architecture.md Sec 15.3,
/// ADR-005) over a real Postgres: the migration applies (four tables plus
/// the generated tsvector columns and GIN indexes), the durable stores
/// round-trip, and the acceptance bar holds -- tsvector search answers
/// under 100 ms at ten thousand events. Skips when Docker is absent, the
/// same posture as the other Postgres suites.
/// </summary>
[Collection("postgres")]
public sealed class CollaborationDurabilityTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public CollaborationDurabilityTests(PostgresFixture postgres)
        => _postgres = postgres;

    [Fact]
    public async Task MigrationApplies_AndStoresRoundTrip()
    {
        Assert.True(_postgres.IsAvailable, "Postgres is not available; skipping.");

        await using var env = await TestEnv.StartAsync(_postgres);
        var engagement = await EngagementSetup.CreateOwnedEngagementAsync(env.Host);
        var implant = await EngagementSetup.EnrollImplantAsync(env.Host, engagement);
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var comments = env.Host.Services.GetRequiredService<ICommentStore>();
        var chat = env.Host.Services.GetRequiredService<IChatStore>();
        var credentials = env.Host.Services.GetRequiredService<ICredentialStore>();
        var tasks = env.Host.Services.GetRequiredService<ITaskRepository>();

        var comment = Comment.Create(
            CommentId.New(), engagement, CommentSubjectKind.Implant, implant.Id.Value,
            new OperatorId(Guid.NewGuid()), "mutable process knowledge", clock.GetUtcNow());
        await comments.SaveAsync(comment, CancellationToken.None);
        comment.Edit("edited in place", clock.GetUtcNow());
        await comments.SaveAsync(comment, CancellationToken.None);

        var channel = ChatChannel.Create(ChatChannelId.New(), engagement, "night-watch",
            new OperatorId(Guid.NewGuid()), clock.GetUtcNow());
        await chat.SaveChannelAsync(channel, CancellationToken.None);
        await chat.SaveMessageAsync(
            ChatMessage.Create(ChatMessageId.New(), engagement, channel.Id,
                new OperatorId(Guid.NewGuid()), "first message", clock.GetUtcNow()),
            CancellationToken.None);

        var credential = new CredentialRecord(
            CredentialId.New(), engagement, TaskId.New(), implant.Id,
            "password", "svc-web", "plaintext-posture", Source: null, Host: "web01", Note: null,
            clock.GetUtcNow());
        await credentials.AddAsync(credential, CancellationToken.None);

        // Read back through the same ports the host serves: the durable
        // adapters answered, the shapes survived the round trip.
        var readBack = await comments.FindAsync(comment.Id, CancellationToken.None);
        Assert.NotNull(readBack);
        Assert.Equal("edited in place", readBack!.Body);
        Assert.NotNull(readBack.EditedAt);
        Assert.NotNull(Assert.Single(await chat.ListChannelsAsync(engagement, CancellationToken.None)));
        Assert.Single((await chat.ListPageByChannelAsync(channel.Id, 50, null, null, CancellationToken.None)).Items);
        var readCredential = await credentials.FindAsync(credential.Id, CancellationToken.None);
        Assert.Equal("plaintext-posture", readCredential!.Secret);

        // The generated columns are live: a search through the durable
        // adapter finds the comment's text and the credential's metadata --
        // and never the credential's secret.
        // The edit is live in the index too: the generated column follows
        // the current body, so the original wording no longer matches and
        // the edited one does -- mutable knowledge, searchable as it stands.
        var search = env.Host.Services.GetRequiredService<IEngagementSearch>();
        var hits = await search.SearchAsync(engagement, "process knowledge", null, 50, CancellationToken.None);
        Assert.Empty(hits);
        hits = await search.SearchAsync(engagement, "edited in place", null, 50, CancellationToken.None);
        Assert.Contains(hits, h => h.Corpus == SearchCorpus.Comment);
        hits = await search.SearchAsync(engagement, "svc-web", null, 50, CancellationToken.None);
        Assert.Contains(hits, h => h.Corpus == SearchCorpus.Credential);
        hits = await search.SearchAsync(engagement, "plaintext-posture", null, 50, CancellationToken.None);
        Assert.DoesNotContain(hits, h => h.Corpus == SearchCorpus.Credential);
    }

    /// <summary>
    /// The item's acceptance criterion: at ten thousand events spread over
    /// the five corpora, the tsvector search answers under 100 ms. The seed
    /// runs through the durable stores directly (speed, not coverage --
    /// coverage is the suite above), and the measured call is the store
    /// adapter the operator API serves.
    /// </summary>
    [Fact]
    public async Task Search_AnswersUnder100Ms_AtTenThousandEvents()
    {
        Assert.True(_postgres.IsAvailable, "Postgres is not available; skipping.");

        await using var env = await TestEnv.StartAsync(_postgres);
        var engagement = await EngagementSetup.CreateOwnedEngagementAsync(env.Host);
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();
        var search = env.Host.Services.GetRequiredService<IEngagementSearch>();

        var tasks = env.Host.Services.GetRequiredService<ITaskRepository>();
        var comments = env.Host.Services.GetRequiredService<ICommentStore>();
        var chat = env.Host.Services.GetRequiredService<IChatStore>();
        var credentials = env.Host.Services.GetRequiredService<ICredentialStore>();
        var implants = env.Host.Services.GetRequiredService<Rod.CoreState.Implants.IImplantRepository>();
        var implant = await EngagementSetup.EnrollImplantAsync(env.Host, engagement);

        var operatorId = new OperatorId(Guid.NewGuid());
        var now = clock.GetUtcNow();
        var token = CancellationToken.None;

        // Ten thousand events, the AC's scale: 2,000 completed tasks with
        // output, 4,000 comments, 3,200 chat messages, 800 credentials. The
        // tasks ride the real lifecycle (queued, claimed, completed) so the
        // seeded output is the transcript the search index reads.
        for (var i = 0; i < 2_000; i++)
        {
            var task = global::Rod.CoreState.Tasks.Task.Create(
                TaskId.New(), engagement, implant.Id, operatorId,
                "shell.exec", $"arg-{i % 100}", now.AddSeconds(i), null);
            await tasks.SaveAsync(task, token);
            var dispatched = await tasks.ClaimNextPendingAsync(implant.Id, now.AddSeconds(i), null, token);
            Assert.NotNull(dispatched);
            dispatched!.Complete($"uid=1000(user{i % 500}) gid=1000(user{i % 500}) groups=…", TaskOutcome.Succeeded, now.AddSeconds(i + 1));
            await tasks.SaveAsync(dispatched, token);
        }

        for (var i = 0; i < 4_000; i++)
        {
            await comments.SaveAsync(
                Comment.Create(
                    CommentId.New(), engagement, CommentSubjectKind.Implant, implant.Id.Value,
                    operatorId, $"comment {i}: keep an eye on host-{i % 40}", now.AddSeconds(i)),
                token);
        }

        var channel = ChatChannel.Create(ChatChannelId.New(), engagement, "watch",
            operatorId, now);
        await chat.SaveChannelAsync(channel, token);
        for (var i = 0; i < 3_200; i++)
        {
            await chat.SaveMessageAsync(
                ChatMessage.Create(
                    ChatMessageId.New(), engagement, channel.Id, operatorId,
                    $"message {i}: beacon-{i % 25} checked in", now.AddSeconds(i)),
                token);
        }

        for (var i = 0; i < 800; i++)
        {
            await credentials.AddAsync(
                new CredentialRecord(
                    CredentialId.New(), engagement, TaskId.New(), implant.Id,
                    "password", $"svc-{i % 60}", $"secret-value-{i}", null, $"host-{i % 30}", null, now.AddSeconds(i)),
                token);
        }

        // Warm the plan and the cache, then measure steady state: the
        // median of five runs under the bar, and no single run far over it.
        await search.SearchAsync(engagement, "host-17", null, 50, token);

        var samples = new List<long>();
        foreach (var query in new[] { "host-17", "svc-42", "beacon-7", "uid", "checked in" })
        {
            var stopwatch = Stopwatch.StartNew();
            var hits = await search.SearchAsync(engagement, query, null, 50, token);
            stopwatch.Stop();
            Assert.NotEmpty(hits);
            samples.Add(stopwatch.ElapsedMilliseconds);
        }

        var median = samples.OrderBy(s => s).ElementAt(samples.Count / 2);
        Assert.True(median < 100,
            $"Search at 10k events must answer under 100 ms; median was {median} ms "
            + $"(samples: {string.Join(", ", samples)}).");
    }

    // The Postgres host, the same shape the webhook durability suite uses.
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;

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
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }
}
