using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Live;

namespace Rod.Operators.Live;

/// <summary>
/// In-memory <see cref="ILiveEventBus"/> for the walking skeleton (roadmap M2.4
/// -- no Postgres / no out-of-process bus yet). Each engagement owns a set of
/// subscriber channels; <see cref="PublishAsync"/> fans an event out to every
/// current subscriber on that engagement, and <see cref="SubscribeAsync"/>
/// yields a per-subscriber stream. State is process-local and lost on restart;
/// the port keeps callers agnostic to that.
///
/// Live state is best-effort: the audit trail (architecture.md Sec 11) is the
/// durable, attributed record, and this bus is the transient projection
/// operators read while connected. A slow subscriber never blocks a publisher:
/// each subscriber's channel is bounded, and a full channel drops the oldest
/// undelivered event rather than stalling the producer or the other peers. The
/// dropped subscriber still sees the latest state on its next list read.
///
/// Engagement isolation is by construction: a subscriber receives only its own
/// engagement's events, because publish and subscribe are both keyed on the
/// engagement id (architecture.md Sec 3).
/// </summary>
public sealed class InMemoryLiveEventBus : ILiveEventBus
{
    // One engagement -> its live subscribers. Each subscriber holds its own
    // bounded channel, so publishers and peers are decoupled per subscriber.
    private readonly ConcurrentDictionary<EngagementId, SubscriberSet> _engagements = new();

    // Per-subscriber capacity. Live events are small and frequent; a bounded
    // channel with drop-oldest keeps a stalled subscriber from blocking the bus
    // while still delivering a recent backlog on a brief stall.
    private const int ChannelCapacity = 256;

    public Task PublishAsync(LiveEvent @event, CancellationToken cancellationToken = default)
    {
        if (_engagements.TryGetValue(@event.EngagementId, out var set))
            set.Publish(@event);

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<LiveEvent> SubscribeAsync(
        EngagementId engagement,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var set = _engagements.GetOrAdd(engagement, _ => new SubscriberSet(ChannelCapacity));
        var subscriber = set.Add();

        try
        {
            await foreach (var item in subscriber.Reader.ReadAllAsync().WithCancellation(cancellationToken))
                yield return item;
        }
        finally
        {
            // Always remove on exit, including cancellation, so the engagement's
            // subscriber set does not leak a dead channel.
            set.Remove(subscriber);
        }
    }

    // Holds one engagement's subscribers and the lock that serializes
    // add/remove/publish against that engagement. Per-engagement locks keep
    // independent engagements from contending.
    private sealed class SubscriberSet
    {
        private readonly List<Channel<LiveEvent>> _subscribers = new();
        private readonly Lock _gate = new();
        private readonly int _capacity;
        private readonly BoundedChannelFullMode _fullMode = BoundedChannelFullMode.DropOldest;

        public SubscriberSet(int capacity) => _capacity = capacity;

        public Channel<LiveEvent> Add()
        {
            var channel = Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(_capacity)
            {
                FullMode = _fullMode,
                SingleReader = true,
                SingleWriter = false,
            });

            lock (_gate)
                _subscribers.Add(channel);

            return channel;
        }

        public void Remove(Channel<LiveEvent> channel)
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
            channel.Writer.TryComplete();
        }

        // Fan-out to every current subscriber. A snapshot is taken under the lock
        // so adds/removes racing this call stay consistent; writes themselves are
        // outside the lock (each channel is independent and non-blocking under
        // DropOldest).
        public void Publish(LiveEvent @event)
        {
            Channel<LiveEvent>[] snapshot;
            lock (_gate)
                snapshot = _subscribers.ToArray();

            foreach (var channel in snapshot)
                channel.Writer.TryWrite(@event);
        }
    }
}
