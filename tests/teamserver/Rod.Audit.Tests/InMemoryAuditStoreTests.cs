using System.Collections.Immutable;

namespace Rod.Audit.Tests;

/// <summary>
/// The  acceptance check for the audit store: the trail is hash-chained per
/// engagement and tamper-evident. Appending the same event twice is rejected
/// (append-only); reads are oldest-first and engagement-scoped; and rewriting a
/// stored event breaks the chain at the next link, which
/// <see cref="AuditChain.VerifyTrail"/> surfaces (architecture.md Sec 11).
/// </summary>
public class InMemoryAuditStoreTests
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

    [Fact]
    public async Task Append_ThreadsEachEvent_OffThePrevious()
    {
        var store = new InMemoryAuditStore();
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();

        var e0 = Fact(engagement, task, 0);
        var e1 = Fact(engagement, task, 1);
        await store.AppendAsync(e0);
        await store.AppendAsync(e1);

        var stored = await store.ListAsync(engagement);
        Assert.Equal(2, stored.Count);

        // The first event follows genesis; the second follows the first. The
        // store stamps both, so the facts passed in carried empty hash fields.
        Assert.Equal(AuditChain.GenesisHash, stored[0].PreviousHash);
        Assert.Equal(stored[0].Hash, stored[1].PreviousHash);
        Assert.NotEmpty(stored[0].Hash);
        Assert.NotEmpty(stored[1].Hash);

        // And the chain verifies clean.
        Assert.Null(AuditChain.VerifyTrail(stored));
    }

    [Fact]
    public async Task Append_RejectsTheSameEventId_Twice()
    {
        var store = new InMemoryAuditStore();
        var engagement = Guid.NewGuid();
        var fact = Fact(engagement, Guid.NewGuid(), 0);

        await store.AppendAsync(fact);

        // Append-only: an event, once written, is never overwritten. The same
        // EventId twice is a programming error, not a silent update.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(fact));
    }

    [Fact]
    public async Task List_SameInstantEvents_ListInAppendOrder_AndVerify()
    {
        // The hash uses millisecond precision, so a busy engagement appends
        // events that share an instant; the listing must still present them in
        // append order or a chain verification walking it reports a break
        // that is not there. The chain links are the order: the listing walks
        // them, the way the file-backed and Postgres adapters do.
        var store = new InMemoryAuditStore();
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();

        var facts = Enumerable.Range(0, 16)
            .Select(seed => AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagement,
                operatorId: Guid.NewGuid(),
                implantId: Guid.NewGuid(),
                taskId: task,
                verb: "shell.exec",
                kind: AuditEventKind.TaskCompleted,
                payload: $"arg-{seed}",
                output: "out",
                outcome: "Succeeded",
                at: T0))
            .ToArray();
        foreach (var fact in facts)
            await store.AppendAsync(fact);

        var stored = await store.ListAsync(engagement);

        Assert.Equal(facts.Length, stored.Count);
        Assert.Equal(facts.Select(f => f.EventId), stored.Select(s => s.EventId));
        Assert.Null(AuditChain.VerifyTrail(stored));
    }

    [Fact]
    public async Task Append_KeepsEachEngagement_AsAnIndependentChain()
    {
        var store = new InMemoryAuditStore();
        var engagementA = Guid.NewGuid();
        var engagementB = Guid.NewGuid();
        var task = Guid.NewGuid();

        // Interleave appends across two engagements; each must still start at
        // genesis, since cross-engagement events never share a hash head.
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
        // The  acceptance: tampering with a stored event is detectable.
        var store = new InMemoryAuditStore();
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();

        await store.AppendAsync(Fact(engagement, task, 0, output: "first"));
        await store.AppendAsync(Fact(engagement, task, 1));
        await store.AppendAsync(Fact(engagement, task, 2));

        var original = await store.ListAsync(engagement);
        Assert.Null(AuditChain.VerifyTrail(original));

        // Simulate a tampered store: rewrite the first event's output in the
        // trail read back, leaving its stored hash untouched. A recomputation no
        // longer matches, so the break surfaces at index 0 (its own hash is
        // wrong) -- and every link after it is wrong by cascade.
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
    public async Task Tamper_BreaksTheChain_WhenALinkIsReordered()
    {
        // Swapping two events shows up as a PreviousHash mismatch: each event's
        // link points at a predecessor that is no longer the one before it.
        var store = new InMemoryAuditStore();
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();

        await store.AppendAsync(Fact(engagement, task, 0));
        await store.AppendAsync(Fact(engagement, task, 1));

        var trail = await store.ListAsync(engagement);
        var reordered = new[] { trail[1], trail[0] }.ToImmutableArray();

        // The first link now claims the second event's hash as its predecessor,
        // which the genesis-start check fails before any hash is recomputed.
        var breakAt = AuditChain.VerifyTrail(reordered);
        Assert.NotNull(breakAt);
        Assert.Equal(0, breakAt!.Index);
        Assert.Equal(ChainBreakKind.PreviousHashMismatch, breakAt.Kind);
    }

    [Fact]
    public async Task Reads_AreScopedAndOldestFirst()
    {
        var store = new InMemoryAuditStore();
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
        // The listing is in append order -- the chain-link order the
        // file-backed and Postgres adapters list in too, and the order a
        // verification walks. At stamps are advisory (concurrent appends can
        // carry slightly out-of-order clocks); the links are the causal order.
        Assert.Equal(new[] { T0, T0.AddSeconds(2), T0.AddSeconds(1) },
            engagementOnly.Select(e => e.At));

        // Engagement B is invisible from engagement A.
        Assert.Single(await store.ListAsync(engagementB));
    }

    [Fact]
    public async Task Find_ReturnsTheChainedEvent_ByExactId()
    {
        var store = new InMemoryAuditStore();
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();
        var fact = Fact(engagement, task, 0);

        await store.AppendAsync(fact);

        var found = await store.FindAsync(fact.EventId);
        Assert.NotNull(found);
        // The stored event carries the stamped hashes, not the empty ones the
        // fact was built with.
        Assert.NotEmpty(found!.Hash);
        Assert.Null(await store.FindAsync(Guid.NewGuid()));
    }
}
