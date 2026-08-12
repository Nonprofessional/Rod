using Rod.CoreState.Engagements;

namespace Rod.CoreState.Live;

/// <summary>
/// Publish/subscribe port for the operator layer's live-state fan-out
/// (architecture.md Sec 4.1, layer 4). Producers (a task being issued, a beacon
/// capturing a result, a presence join) publish <see cref="LiveEvent"/>s;
/// connected operator sessions subscribe to their engagement's stream and push
/// each event to the client.
///
/// The port lives in core state, beside <see cref="Sessions.ISessionRegistry"/>,
/// because the transport layer terminates the SSE endpoint and must not depend
/// on the operator layer (architecture test LayerDependencyTests allows
/// transport only core state / protocol / audit). The implementation lives in
/// <c>Rod.Operators</c>; this contract keeps callers agnostic to it.
///
/// Engagement isolation is by construction: <see cref="SubscribeAsync"/> takes
/// an engagement id and receives only that engagement's events. Cross-engagement
/// access never reaches this with another engagement's id (architecture.md
/// Sec 3).
///
/// The bus is best-effort and transient: it rebuilds its projection from current
/// state on reconnect and never substitutes for the audit trail (Sec 11), which
/// is the durable, attributed record.
/// </summary>
public interface ILiveEventBus
{
    /// <summary>
    /// Publishes <paramref name="event"/> to every subscriber on its engagement.
    /// Fan-out: all connected operators on that engagement observe it.
    /// </summary>
    Task PublishAsync(LiveEvent @event, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to the engagement's live stream. Yields events as they are
    /// published, until the cancellation token fires (the operator disconnected).
    /// Per-engagement: only this engagement's events are delivered.
    /// </summary>
    IAsyncEnumerable<LiveEvent> SubscribeAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default);
}
