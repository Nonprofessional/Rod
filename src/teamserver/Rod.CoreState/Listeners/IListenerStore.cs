namespace Rod.CoreState.Listeners;

/// <summary>
/// The durable home for engagement-scoped listener definitions. The transport
/// layer saves a definition once its socket is bound and removes it when the
/// listener is deleted; a restart reads the list back and rebinds each entry.
/// In-memory by default (definitions live as long as the process -- the same
/// posture as the rest of core state without Postgres); the Postgres-backed
/// adapter swaps in when <c>ConnectionStrings:Postgres</c> is set.
/// </summary>
public interface IListenerStore
{
    /// <summary>Upserts a definition (the id is the identity; a repoint re-saves).</summary>
    Task SaveAsync(ListenerDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>The definition, or null when unknown.</summary>
    Task<ListenerDefinition?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Every stored definition, ordered by creation time.</summary>
    Task<IReadOnlyList<ListenerDefinition>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The definitions belonging to one engagement, ordered by creation time.</summary>
    Task<IReadOnlyList<ListenerDefinition>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a definition. Returns false when it was not stored.</summary>
    Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}
