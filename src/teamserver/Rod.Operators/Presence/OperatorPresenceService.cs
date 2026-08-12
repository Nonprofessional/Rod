using System.Collections.Concurrent;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Live;

namespace Rod.Operators.Presence;

/// <summary>
/// Tracks which operators currently have a live session open on each engagement
/// (architecture.md Sec 4.1, layer 4). A session opens when an operator
/// connects to the engagement's event stream and closes on disconnect; the
/// roster is the operator-visible "who is online" projection. Presence is
/// ephemeral -- the audit trail (Sec 11) remains the attributed record.
///
/// Identity is supplied by the caller (the SSE endpoint) from query parameters
/// in this milestone; real operator authentication arrives later and replaces
/// only how the identity is established, not this roster.
/// </summary>
public sealed class OperatorPresenceService
{
    private readonly ConcurrentDictionary<EngagementId, EngagementRoster> _engagements = new();
    private readonly ILiveEventBus _bus;
    private readonly TimeProvider _clock;

    public OperatorPresenceService(ILiveEventBus bus, TimeProvider clock)
    {
        _bus = bus;
        _clock = clock;
    }

    /// <summary>
    /// Records that <paramref name="operator"/> joined <paramref name="engagement"/>
    /// and publishes an <see cref="LiveEventKind.OperatorJoined"/> event to peers.
    /// A reconnect from the same operator refreshes its seen-at rather than
    /// duplicating the entry.
    /// </summary>
    public async Task JoinAsync(
        EngagementId engagement,
        OperatorSnapshot @operator,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var set = _engagements.GetOrAdd(engagement, _ => new EngagementRoster());
        var joined = set.Join(@operator);

        if (joined)
        {
            await _bus.PublishAsync(
                LiveEvent.Presence(engagement, LiveEventKind.OperatorJoined, @operator.Id, @operator.Handle, now),
                cancellationToken);
        }
    }

    /// <summary>
    /// Records that <paramref name="operator"/> left <paramref name="engagement"/>
    /// and publishes an <see cref="LiveEventKind.OperatorLeft"/> event to peers.
    /// A no-op when the operator was not present (e.g. a duplicate close).
    /// </summary>
    public async Task LeaveAsync(
        EngagementId engagement,
        OperatorId operatorId,
        CancellationToken cancellationToken = default)
    {
        if (!_engagements.TryGetValue(engagement, out var set))
            return;

        var now = _clock.GetUtcNow();
        var (left, handle) = set.Leave(operatorId);
        if (left)
        {
            await _bus.PublishAsync(
                LiveEvent.Presence(engagement, LiveEventKind.OperatorLeft, operatorId, handle, now),
                cancellationToken);
        }
    }

    /// <summary>The operators currently online on an engagement.</summary>
    public Task<IReadOnlyList<OperatorSnapshot>> ListAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        if (!_engagements.TryGetValue(engagement, out var set))
            return Task.FromResult<IReadOnlyList<OperatorSnapshot>>(Array.Empty<OperatorSnapshot>());

        return Task.FromResult(set.Snapshot());
    }

    /// <summary>
    /// An operator's live identity for presence: its id, handle, and display
    /// name. Carries only what the peers' "who is online" view needs.
    /// </summary>
    public sealed record OperatorSnapshot(OperatorId Id, string Handle, string DisplayName);

    // One engagement's presence roster. Idempotent on join/leave so a flap does
    // not duplicate entries or emit spurious events; the lock serializes the
    // join/leave/snapshot against that engagement only.
    private sealed class EngagementRoster
    {
        private readonly Dictionary<OperatorId, OperatorSnapshot> _operators = new();
        private readonly Lock _gate = new();

        // True when the operator was not already present (so the caller should
        // emit a join event); false on a duplicate join.
        public bool Join(OperatorSnapshot @operator)
        {
            lock (_gate)
            {
                return _operators.TryAdd(@operator.Id, @operator);
            }
        }

        // Returns (left, handle): left is true when the operator was present (so
        // the caller should emit a leave event); handle is the handle to
        // attribute the event with, empty when unknown.
        public (bool Left, string Handle) Leave(OperatorId operatorId)
        {
            lock (_gate)
            {
                if (_operators.Remove(operatorId, out var snapshot))
                    return (true, snapshot.Handle);
                return (false, string.Empty);
            }
        }

        public IReadOnlyList<OperatorSnapshot> Snapshot()
        {
            lock (_gate)
                return _operators.Values.ToArray();
        }
    }
}
