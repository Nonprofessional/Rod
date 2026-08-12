namespace Rod.Audit.Tests;

/// <summary>
/// The pure chain math, independent of any store (storage &amp; audit layer,
/// roadmap M2.3). Each event commits to its predecessor; the first link of an
/// engagement starts at the genesis hash; the canonical form is stable. These
/// hold regardless of the storage adapter, so they live against
/// <see cref="AuditChain"/> directly.
/// </summary>
public class AuditChainTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static AuditEvent Fact(Guid engagement, int seed, string output = "out")
        => AuditEvent.Fact(
            eventId: Guid.NewGuid(),
            engagementId: engagement,
            operatorId: Guid.NewGuid(),
            implantId: Guid.NewGuid(),
            taskId: Guid.NewGuid(),
            verb: "shell.exec",
            kind: AuditEventKind.TaskCompleted,
            payload: $"arg-{seed}",
            output: output,
            outcome: "Succeeded",
            at: T0.AddSeconds(seed));

    [Fact]
    public void Chain_FirstEvent_FollowsGenesis()
    {
        var engagement = Guid.NewGuid();
        var linked = AuditChain.Chain(Fact(engagement, 0), AuditChain.GenesisHash);

        Assert.Equal(AuditChain.GenesisHash, linked.PreviousHash);
        Assert.NotEmpty(linked.Hash);
        Assert.NotEqual(AuditChain.GenesisHash, linked.Hash);
    }

    [Fact]
    public void Chain_SecondEvent_PointsAtFirst()
    {
        var engagement = Guid.NewGuid();
        var first = AuditChain.Chain(Fact(engagement, 0), AuditChain.GenesisHash);
        var second = AuditChain.Chain(Fact(engagement, 1), first.Hash);

        Assert.Equal(first.Hash, second.PreviousHash);
    }

    [Fact]
    public void ComputeHash_IsDeterministic_ForIdenticalEvents()
    {
        var engagement = Guid.NewGuid();
        var linked = AuditChain.Chain(Fact(engagement, 0), AuditChain.GenesisHash);

        // The same event contents hash the same every time; the hash is a pure
        // function of the event, not of when it is computed.
        var recomputed = AuditChain.ComputeHash(linked);

        Assert.Equal(linked.Hash, recomputed);
    }

    [Fact]
    public void ComputeHash_Changes_WhenAFieldChanges()
    {
        var engagement = Guid.NewGuid();
        var linked = AuditChain.Chain(Fact(engagement, 0, output: "first"), AuditChain.GenesisHash);

        var tampered = linked with { Output = "second" };
        var tamperedHash = AuditChain.ComputeHash(tampered with { PreviousHash = linked.PreviousHash });

        Assert.NotEqual(linked.Hash, tamperedHash);
    }

    [Fact]
    public void ComputeHash_Changes_WhenPreviousHashChanges()
    {
        var engagement = Guid.NewGuid();
        var linked = AuditChain.Chain(Fact(engagement, 0), AuditChain.GenesisHash);

        // Same contents but chained off a different predecessor -> different hash.
        // This is what makes each event bind to the one before it.
        var reLinked = AuditChain.ComputeHash(linked with { PreviousHash = new string('a', 64) });

        Assert.NotEqual(linked.Hash, reLinked);
    }
}
