using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Nodes;

namespace Rod.Audit.Tests;

/// <summary>
/// The M6.4 acceptance check for the durable audit store: the trail is
/// hash-chained per engagement, tamper-evident, and -- the new property --
/// survives disposal and recreation of the store, so a restarted teamserver
/// continues each engagement's chain off its last stored event. This mirrors
/// <see cref="InMemoryAuditStoreTests"/> against a temp directory, then adds the
/// reload cases that make retention real (architecture.md Sec 11; roadmap M6.4).
/// </summary>
public class FileAuditStoreTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static AuditEvent Fact(Guid engagement, Guid task, int seed, string output = "out")
        => AuditEvent.Fact(
            eventId: Guid.NewGuid(),
            engagementId: engagement,
            operatorId: Guid.NewGuid(),
            implantId: Guid.NewGuid(),
            taskId: task,
            verb: "shell.exec",
            kind: AuditEventKind.TaskCompleted,
            payload: $"arg-{seed}",
            output: output,
            outcome: "Succeeded",
            at: T0.AddSeconds(seed));

    // A unique temp directory per test, cleaned up on disposal. The store creates
    // the directory lazily, so an empty root is the right starting point.
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "rod-audit-test-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private static AuditPersistenceOptions Options(string dir)
        => new() { DataDirectory = dir };

    [Fact]
    public async Task Append_ThreadsEachEvent_OffThePrevious()
    {
        using var dir = new TempDir();
        var store = new FileAuditStore(Options(dir.Path));
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();

        var e0 = Fact(engagement, task, 0);
        var e1 = Fact(engagement, task, 1);
        await store.AppendAsync(e0);
        await store.AppendAsync(e1);

        var stored = await store.ListAsync(engagement);
        Assert.Equal(2, stored.Count);

        Assert.Equal(AuditChain.GenesisHash, stored[0].PreviousHash);
        Assert.Equal(stored[0].Hash, stored[1].PreviousHash);
        Assert.NotEmpty(stored[0].Hash);
        Assert.NotEmpty(stored[1].Hash);

        Assert.Null(AuditChain.VerifyTrail(stored));
    }

    [Fact]
    public async Task Append_RejectsTheSameEventId_Twice()
    {
        using var dir = new TempDir();
        var store = new FileAuditStore(Options(dir.Path));
        var engagement = Guid.NewGuid();
        var fact = Fact(engagement, Guid.NewGuid(), 0);

        await store.AppendAsync(fact);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(fact));
    }

    [Fact]
    public async Task Append_KeepsEachEngagement_AsAnIndependentChain()
    {
        using var dir = new TempDir();
        var store = new FileAuditStore(Options(dir.Path));
        var engagementA = Guid.NewGuid();
        var engagementB = Guid.NewGuid();
        var task = Guid.NewGuid();

        await store.AppendAsync(Fact(engagementA, task, 0));
        await store.AppendAsync(Fact(engagementB, task, 0));
        await store.AppendAsync(Fact(engagementA, task, 1));

        var a = await store.ListAsync(engagementA);
        var b = await store.ListAsync(engagementB);

        Assert.Equal(AuditChain.GenesisHash, a[0].PreviousHash);
        Assert.Equal(AuditChain.GenesisHash, b[0].PreviousHash);
        Assert.Equal(a[0].Hash, a[1].PreviousHash);
        Assert.Single(b);
    }

    [Fact]
    public async Task Tamper_BreaksTheChain_AtTheNextLink()
    {
        using var dir = new TempDir();
        var store = new FileAuditStore(Options(dir.Path));
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();

        await store.AppendAsync(Fact(engagement, task, 0, output: "first"));
        await store.AppendAsync(Fact(engagement, task, 1));
        await store.AppendAsync(Fact(engagement, task, 2));

        var original = await store.ListAsync(engagement);
        Assert.Null(AuditChain.VerifyTrail(original));

        // A tampered record read back: rewrite the first event's output, leaving
        // its stored hash untouched. The recomputation no longer matches, so the
        // break surfaces at index 0. The chain catches it the same way the
        // in-memory adapter does.
        var tamperedFirst = original[0] with { Output = "forged" };
        var tamperedTrail = new[] { tamperedFirst }
            .Concat(original.Skip(1))
            .ToArray();

        var breakAt = AuditChain.VerifyTrail(tamperedTrail);

        Assert.NotNull(breakAt);
        Assert.Equal(0, breakAt!.Index);
        Assert.Equal(ChainBreakKind.HashMismatch, breakAt.Kind);
    }

    [Fact]
    public async Task Reload_RefusesToServe_AnEngagementWhoseChainWasTampered()
    {
        using var dir = new TempDir();
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();

        var store = new FileAuditStore(Options(dir.Path));
        await store.AppendAsync(Fact(engagement, task, 0));
        await store.AppendAsync(Fact(engagement, task, 1));
        await store.AppendAsync(Fact(engagement, task, 2));

        // Forge the first record on disk: its stored hash no longer matches its
        // contents, so the chain fails verification at recovery. A fresh store
        // over the same directory must refuse the engagement's trail rather than
        // serve tampered evidence.
        var path = Path.Combine(dir.Path, "audit.jsonl");
        var lines = File.ReadAllLines(path);
        var node = JsonNode.Parse(lines[0])!.AsObject();
        node["output"] = "forged";
        lines[0] = node.ToJsonString();
        File.WriteAllLines(path, lines);

        var reloaded = new FileAuditStore(Options(dir.Path));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await reloaded.ListAsync(engagement));

        Assert.Contains("verification failed", ex.Message);
    }

    [Fact]
    public async Task Reads_AreScopedAndOldestFirst()
    {
        using var dir = new TempDir();
        var store = new FileAuditStore(Options(dir.Path));
        var engagementA = Guid.NewGuid();
        var engagementB = Guid.NewGuid();
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();

        await store.AppendAsync(Fact(engagementA, taskA, 0));
        await store.AppendAsync(Fact(engagementA, taskA, 2));
        await store.AppendAsync(Fact(engagementA, taskB, 1));
        await store.AppendAsync(Fact(engagementB, taskB, 3));

        var forTaskA = await store.ForTaskAsync(taskA);
        Assert.Equal(2, forTaskA.Count);
        Assert.All(forTaskA, e => Assert.Equal(taskA, e.TaskId));
        Assert.True(forTaskA[0].At < forTaskA[1].At);

        var engagementOnly = await store.ListAsync(engagementA);
        Assert.Equal(3, engagementOnly.Count);
        Assert.All(engagementOnly, e => Assert.Equal(engagementA, e.EngagementId));
        Assert.True(engagementOnly[0].At < engagementOnly[1].At);
        Assert.True(engagementOnly[1].At < engagementOnly[2].At);

        Assert.Single(await store.ListAsync(engagementB));
    }

    [Fact]
    public async Task Find_ReturnsTheChainedEvent_ByExactId()
    {
        using var dir = new TempDir();
        var store = new FileAuditStore(Options(dir.Path));
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();
        var fact = Fact(engagement, task, 0);

        await store.AppendAsync(fact);

        var found = await store.FindAsync(fact.EventId);
        Assert.NotNull(found);
        Assert.NotEmpty(found!.Hash);
        Assert.Null(await store.FindAsync(Guid.NewGuid()));
    }

    // The M6.4 property: a fresh store over the same directory recovers each
    // engagement's chain head and continues the trail off its last stored event,
    // rather than restarting the chain. A restarted teamserver behaves exactly
    // like one that never stopped.
    [Fact]
    public async Task Reload_ContinuesTheChain_OffTheStoredHead()
    {
        using var dir = new TempDir();
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();

        var first = Fact(engagement, task, 0);
        var second = Fact(engagement, task, 1);

        // The store flushes each append, so there is nothing to dispose between
        // instances -- constructing a new one over the same dir is the restart.
        {
            var storeA = new FileAuditStore(Options(dir.Path));
            await storeA.AppendAsync(first);
            await storeA.AppendAsync(second);
        }

        // A brand-new instance over the same directory: no in-memory head, no
        // knowledge of what came before. It must recover the head and link.
        var storeB = new FileAuditStore(Options(dir.Path));
        var third = Fact(engagement, task, 2);
        await storeB.AppendAsync(third);

        var trail = await storeB.ListAsync(engagement);
        Assert.Equal(3, trail.Count);

        Assert.Equal(AuditChain.GenesisHash, trail[0].PreviousHash);
        Assert.Equal(trail[0].Hash, trail[1].PreviousHash);
        Assert.Equal(trail[1].Hash, trail[2].PreviousHash);

        // The whole trail verifies after the restart -- the stored chain is
        // self-checking across the teardown.
        Assert.Null(AuditChain.VerifyTrail(trail));
    }

    // A duplicate EventId that landed before this process started is still
    // refused: the recovered index carries it, so append-only holds across a
    // restart, not just within one process.
    [Fact]
    public async Task Reload_StillRejectsADuplicateEventId_FromBefore()
    {
        using var dir = new TempDir();
        var engagement = Guid.NewGuid();
        var fact = Fact(engagement, Guid.NewGuid(), 0);

        {
            var storeA = new FileAuditStore(Options(dir.Path));
            await storeA.AppendAsync(fact);
        }

        var storeB = new FileAuditStore(Options(dir.Path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storeB.AppendAsync(fact));
    }

    // An engagement that has no trail on disk starts at genesis after a restart,
    // so a previously-unseen engagement cannot accidentally chain off another.
    [Fact]
    public async Task Reload_NewEngagementStarts_AtGenesis()
    {
        using var dir = new TempDir();
        var oldEngagement = Guid.NewGuid();
        var newEngagement = Guid.NewGuid();
        var task = Guid.NewGuid();

        {
            var storeA = new FileAuditStore(Options(dir.Path));
            await storeA.AppendAsync(Fact(oldEngagement, task, 0));
        }

        var storeB = new FileAuditStore(Options(dir.Path));
        await storeB.AppendAsync(Fact(newEngagement, task, 0));

        var trail = await storeB.ListAsync(newEngagement);
        Assert.Single(trail);
        Assert.Equal(AuditChain.GenesisHash, trail[0].PreviousHash);
    }

    // An empty directory (no audit.jsonl yet) reads as an empty trail, not an
    // error -- the store has simply never been written to.
    [Fact]
    public async Task EmptyDirectory_ReadsAsAnEmptyTrail()
    {
        using var dir = new TempDir();
        var store = new FileAuditStore(Options(dir.Path));

        Assert.Empty(await store.ListAsync(Guid.NewGuid()));
        Assert.Null(await store.FindAsync(Guid.NewGuid()));
    }

    // The first append creates the data directory if it does not already exist.
    [Fact]
    public async Task FirstAppend_CreatesTheDataDirectory()
    {
        using var dir = new TempDir();
        var store = new FileAuditStore(Options(dir.Path));
        Assert.False(Directory.Exists(dir.Path));

        await store.AppendAsync(Fact(Guid.NewGuid(), Guid.NewGuid(), 0));

        Assert.True(Directory.Exists(dir.Path));
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "audit.jsonl")));
    }
}
