namespace Rod.Audit;

/// <summary>
/// Append-only audit trail port (architecture.md Sec 11). Every privileged
/// action appends one <see cref="AuditEvent"/>; the trail is per-engagement and
/// never deletable mid-operation (chain-of-custody). The walking skeleton ships
/// an in-memory implementation; the port keeps callers agnostic to that.
///
/// The audit layer is the innermost ring (architecture.md Sec 4.1): it depends
/// on nothing in-house, so events carry plain <see cref="Guid"/> identifiers
/// rather than the core-state typed ids -- the layer boundary is crossed with
/// primitives, not by importing core-state types.
///
/// Hash-chaining (where tampering breaks the chain) is the concern; the
/// port shape is stable for it. <see cref="AppendAsync"/> is the only mutating
/// operation by contract -- nothing here removes or rewrites events.
/// </summary>
public interface IAuditStore
{
    /// <summary>
    /// Appends <paramref name="event"/> to the trail. Append-only: an event, once
    /// written, is never removed or modified.
    /// </summary>
    Task AppendAsync(AuditEvent @event, CancellationToken cancellationToken = default);

    /// <summary>An event's full trail entry, or null when no event has that id.</summary>
    Task<AuditEvent?> FindAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Events for a task, oldest first -- the attributed record of what an
    /// implant was told to do and what came back.
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> ForTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The engagement's event trail, oldest first. Per-engagement by
    /// construction; cross-engagement access never reaches this with another
    /// engagement's id.
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> ListAsync(Guid engagementId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the engagement's event trail (architecture.md Sec 11): the
    /// newest <paramref name="limit"/> events, or the next older page when
    /// <paramref name="cursor"/> carries the previous page's
    /// <see cref="AuditPage.NextCursor"/>. A long engagement no longer grows the
    /// listing endpoint without bound -- the operator UI walks pages.
    /// </summary>
    Task<AuditPage> ListPageAsync(
        Guid engagementId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);
}
