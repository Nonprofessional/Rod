using System.Collections.Concurrent;
using Rod.CoreState;
using Rod.CoreState.Live;
using Rod.Operators.Live;
using Task = System.Threading.Tasks.Task;

namespace Rod.Operators.Tests;

/// <summary>
/// Multi-threaded hammer tests for <see cref="InMemoryLiveEventBus"/> fan-out
/// (architecture.md Sec 4.1, layer 4). Subscribe and publish race through a
/// per-engagement gate; these tests drive real threads into it and assert
/// delivery stays engagement-scoped and duplicate-free, and that churning
/// subscribers cannot break the bus.
/// </summary>
public class LiveEventBusConcurrencyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static LiveEvent Event(EngagementId engagement, string payload)
        => LiveEvent.Presence(engagement, LiveEventKind.OperatorJoined, OperatorId.New(), payload, Now);

    // Consumes one subscription into a queue until canceled. The subscribe
    // finally-block removes the subscriber, so a completed consumer leaves no
    // dead channel behind.
    private static async Task ConsumeAsync(
        ILiveEventBus bus,
        EngagementId engagement,
        CancellationToken ct,
        ConcurrentQueue<string> received)
    {
        try
        {
            await foreach (var e in bus.SubscribeAsync(engagement, ct))
                received.Enqueue(e.Payload);
        }
        catch (OperationCanceledException)
        {
            // Expected teardown: the subscription ends when its token fires.
        }
    }

    // Publishes probe events until the subscriber's queue shows one: a
    // subscriber set only fans out to subscribers attached before the publish,
    // so a burst must not start before the subscription is in place.
    private static async Task AttachAsync(
        ILiveEventBus bus,
        EngagementId engagement,
        ConcurrentQueue<string> received,
        string probePrefix)
    {
        for (var round = 0; round < 100 && !received.Any(p => p.StartsWith(probePrefix)); round++)
        {
            await bus.PublishAsync(Event(engagement, $"{probePrefix}{round}"));
            await Task.Delay(1);
        }

        Assert.True(received.Any(p => p.StartsWith(probePrefix)),
            "Subscriber never attached to the engagement.");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task ConcurrentPublish_FansOutEveryEventOnce_PerEngagement()
    {
        var bus = new InMemoryLiveEventBus();
        var engagementA = EngagementId.New();
        var engagementB = EngagementId.New();

        var receivedA1 = new ConcurrentQueue<string>();
        var receivedA2 = new ConcurrentQueue<string>();
        var receivedB = new ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource();

        var consumerA1 = Task.Run(() => ConsumeAsync(bus, engagementA, cts.Token, receivedA1));
        var consumerA2 = Task.Run(() => ConsumeAsync(bus, engagementA, cts.Token, receivedA2));
        var consumerB = Task.Run(() => ConsumeAsync(bus, engagementB, cts.Token, receivedB));

        // Attach every subscriber before its engagement's burst starts.
        await AttachAsync(bus, engagementA, receivedA1, "probe-");
        await AttachAsync(bus, engagementA, receivedA2, "probe-");
        await AttachAsync(bus, engagementB, receivedB, "bprobe-");

        // The burst stays under the per-subscriber channel capacity (256) while
        // the consumers drain, so nothing can be dropped even if a consumer
        // stalls for the whole burst.
        const int burstCount = 200;
        const int publisherCount = 8;
        var gate = new ManualResetEventSlim(initialState: false);
        var publishers = Enumerable.Range(0, publisherCount).Select(p => Task.Run(async () =>
        {
            gate.Wait();
            for (var i = p; i < burstCount; i += publisherCount)
                await bus.PublishAsync(Event(engagementA, $"evt-{i}"));
        })).ToArray();
        gate.Set();
        await Task.WhenAll(publishers);

        var bBurst = Enumerable.Range(0, 5).Select(i =>
            Task.Run(() => bus.PublishAsync(Event(engagementB, $"bevt-{i}"))));
        await Task.WhenAll(bBurst);

        // Every A subscriber gets every burst event, each exactly once; the B
        // subscriber gets only B's events, and neither side leaks across.
        Assert.True(await WaitUntilAsync(() => receivedA1.Count(p => p.StartsWith("evt-")) == burstCount));
        Assert.True(await WaitUntilAsync(() => receivedA2.Count(p => p.StartsWith("evt-")) == burstCount));
        Assert.True(await WaitUntilAsync(() => receivedB.Count(p => p.StartsWith("bevt-")) == 5));

        Assert.Equal(burstCount, receivedA1.Count(p => p.StartsWith("evt-")));
        Assert.Equal(burstCount, receivedA1.Where(p => p.StartsWith("evt-")).Distinct().Count());
        Assert.Equal(burstCount, receivedA2.Count(p => p.StartsWith("evt-")));
        Assert.Equal(burstCount, receivedA2.Where(p => p.StartsWith("evt-")).Distinct().Count());
        Assert.DoesNotContain(receivedA1, p => p.StartsWith("bevt-"));
        Assert.DoesNotContain(receivedA2, p => p.StartsWith("bevt-"));
        Assert.All(receivedB, p => Assert.True(p.StartsWith("bprobe-") || p.StartsWith("bevt-")));
        Assert.Equal(5, receivedB.Count(p => p.StartsWith("bevt-")));

        // Teardown: cancellation ends every subscription and the consumers exit.
        cts.Cancel();
        await Task.WhenAll(consumerA1, consumerA2, consumerB);
    }

    [Fact]
    public async Task ConcurrentSubscribersChurning_WhilePublishing_LeavesTheBusFunctional()
    {
        var bus = new InMemoryLiveEventBus();
        var engagement = EngagementId.New();

        const int publisherCount = 4;
        const int churnerCount = 4;
        const int churnRounds = 50;
        const int perPublisher = 250;
        var gate = new ManualResetEventSlim(initialState: false);

        var publishers = Enumerable.Range(0, publisherCount).Select(p => Task.Run(async () =>
        {
            gate.Wait();
            for (var i = 0; i < perPublisher; i++)
                await bus.PublishAsync(Event(engagement, $"evt-{p}-{i}"));
        })).ToArray();

        // Short-lived subscriptions that attach, drain, and leave while the
        // publishers hammer the engagement: the add/remove/publish gate and the
        // set-retire race are the paths under test.
        var churners = Enumerable.Range(0, churnerCount).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            for (var round = 0; round < churnRounds; round++)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
                try
                {
                    await foreach (var _ in bus.SubscribeAsync(engagement, cts.Token))
                    {
                        // Drain whatever arrives; the token ends the round.
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected: the short-lived subscription ends.
                }
            }
        })).ToArray();

        gate.Set();
        await Task.WhenAll(publishers.Concat(churners));

        // The churn left no dead subscriber set behind: a fresh subscriber still
        // receives a fresh event, published after it attached.
        var received = new ConcurrentQueue<string>();
        using var cts2 = new CancellationTokenSource();
        var consumer = Task.Run(() => ConsumeAsync(bus, engagement, cts2.Token, received));

        await AttachAsync(bus, engagement, received, "fresh-");
        await bus.PublishAsync(Event(engagement, "final"));
        Assert.True(await WaitUntilAsync(() => received.Contains("final")));

        cts2.Cancel();
        await consumer;
    }
}
