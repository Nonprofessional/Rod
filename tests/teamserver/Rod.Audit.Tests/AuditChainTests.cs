namespace Rod.Audit.Tests;

/// <summary>
/// The pure chain math, independent of any store (storage &amp; audit layer).
/// Each event commits to its predecessor; the first link of an
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
    public void KeyedChain_LinksAreHmac_VerifyOnlyWithTheKey()
    {
        var engagement = Guid.NewGuid();
        var key = Convert.FromBase64String("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");

        var first = AuditChain.Chain(Fact(engagement, 0), AuditChain.GenesisHash, key);
        var second = AuditChain.Chain(Fact(engagement, 1), first.Hash, key);
        var trail = new[] { first, second };

        // Keyed links are distinguishable on the stored form and verify
        // with the key; without it they refuse rather than wave through.
        Assert.StartsWith(AuditChain.KeyedHashPrefix, first.Hash);
        Assert.Null(AuditChain.VerifyTrail(trail, key));
        Assert.NotNull(AuditChain.VerifyTrail(trail));
        // A different key is not the key.
        Assert.NotNull(AuditChain.VerifyTrail(trail, new byte[32]));
    }

    [Fact]
    public void KeyedChain_AConsistentRewriteWithoutTheKey_FailsVerification()
    {
        // The attack the key closes: an attacker holding only the storage
        // rewrites the whole engagement consistently. Unkeyed, that passes;
        // keyed, they cannot produce a single valid link.
        var engagement = Guid.NewGuid();
        var key = new byte[32];

        var first = AuditChain.Chain(Fact(engagement, 0), AuditChain.GenesisHash, key);
        var forged = AuditChain.Chain(
            Fact(engagement, 0) with { Payload = "forged" }, first.PreviousHash);
        Assert.Null(AuditChain.VerifyTrail(new[] { forged }));
        Assert.NotNull(AuditChain.VerifyTrail(new[] { forged }, key));
    }

    [Fact]
    public void MixedTrail_UnkeyedPastVerifies_AndKeyedLinksCannotDowngrade()
    {
        // A trail that predates keying: the unkeyed prefix still verifies,
        // the keyed suffix needs the key, and a bare link after a keyed one
        // is refused -- the writer never goes back.
        var engagement = Guid.NewGuid();
        var key = new byte[32];

        var plainFirst = AuditChain.Chain(Fact(engagement, 0), AuditChain.GenesisHash);
        var keyedSecond = AuditChain.Chain(Fact(engagement, 1), plainFirst.Hash, key);
        Assert.Null(AuditChain.VerifyTrail(new[] { plainFirst, keyedSecond }, key));

        // The downgrade: the keyed link rewritten as a consistent bare link.
        var bareSecond = AuditChain.Chain(Fact(engagement, 1), plainFirst.Hash);
        var downgraded = new[] { plainFirst, bareSecond };
        // It looks fine unkeyed -- but a verifier holding the key never
        // accepted a keyed trail to begin with, and one asked to verify
        // with a key sees a bare link where the deployment writes keyed
        // ones: the mix itself is the tell, caught once a keyed link
        // existed in the original. Verify the real downgrade shape: a keyed
        // first link, then a bare one.
        var keyedFirst = AuditChain.Chain(Fact(engagement, 0), AuditChain.GenesisHash, key);
        var afterDowngrade = new[] { keyedFirst, AuditChain.Chain(Fact(engagement, 1), keyedFirst.Hash) };
        Assert.NotNull(AuditChain.VerifyTrail(afterDowngrade, key));
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
