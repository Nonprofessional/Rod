using Task = System.Threading.Tasks.Task;

namespace Rod.Audit.Tests;

/// <summary>
/// Multi-threaded hammer tests for <see cref="InMemoryAuditStore"/> append
/// atomicity (architecture.md Sec 11). The append and the per-engagement head
/// advance run under one lock so concurrent appends serialize correctly within
/// an engagement; these tests drive real threads into it and prove the
/// resulting trail is one valid hash chain.
/// </summary>
public class InMemoryAuditStoreConcurrencyTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static AuditEvent Fact(Guid engagement, int seed)
        => AuditEvent.Fact(
            eventId: Guid.NewGuid(),
            engagementId: engagement,
            operatorId: Guid.NewGuid(),
            implantId: Guid.NewGuid(),
            taskId: Guid.NewGuid(),
            verb: "shell.exec",
            kind: AuditEventKind.TaskCompleted,
            payload: $"arg-{seed}",
            output: "out",
            outcome: "Succeeded",
            at: T0.AddSeconds(seed));

    [Fact]
    public async Task ConcurrentAppends_OneEngagement_FormOneValidChain()
    {
        var store = new InMemoryAuditStore();
        var engagement = Guid.NewGuid();

        const int eventCount = 300;
        const int writerCount = 12;
        var facts = Enumerable.Range(0, eventCount).Select(i => Fact(engagement, i)).ToArray();

        var gate = new ManualResetEventSlim(initialState: false);
        var writers = Enumerable.Range(0, writerCount).Select(w => Task.Run(async () =>
        {
            gate.Wait();
            for (var i = w; i < facts.Length; i += writerCount)
                await store.AppendAsync(facts[i]);
        })).ToArray();

        gate.Set();
        await Task.WhenAll(writers);

        var stored = (await store.ListAsync(engagement)).ToArray();
        Assert.Equal(eventCount, stored.Length);
        Assert.All(stored, e => Assert.NotEmpty(e.Hash));

        // Reconstruct the chain by following PreviousHash links from genesis.
        // A lost update (two appends sharing one head) would strand or fork the
        // chain, and the walk below would stop short of covering every event.
        var byPrevious = stored.ToLookup(e => e.PreviousHash);
        var chain = new List<AuditEvent>(stored.Length);
        var cursor = AuditChain.GenesisHash;
        for (var i = 0; i < stored.Length; i++)
        {
            var next = Assert.Single(byPrevious[cursor]);
            chain.Add(next);
            cursor = next.Hash;
        }

        Assert.Null(AuditChain.VerifyTrail(chain));
    }

    [Fact]
    public async Task ConcurrentAppends_OfTheSameEventId_AreRejectedAfterTheFirst()
    {
        var store = new InMemoryAuditStore();
        var engagement = Guid.NewGuid();
        var fact = Fact(engagement, 0);

        const int writers = 12;
        var gate = new ManualResetEventSlim(initialState: false);
        var pending = Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            try
            {
                await store.AppendAsync(fact);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        })).ToArray();

        // Release the writers together so they genuinely race for the append
        // lock, then collect the outcomes.
        gate.Set();
        var outcomes = await Task.WhenAll(pending);

        // Append-only holds under contention: exactly one writer wins and the
        // trail holds the event once.
        Assert.Equal(1, outcomes.Count(o => o));
        Assert.Single(await store.ListAsync(engagement));
    }
}
