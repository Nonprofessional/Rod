namespace Rod.CoreState.Engagements;

/// <summary>
/// Persistence port for engagement memberships (architecture.md Sec 3). A
/// membership is engagement state -- unlike operator account state, changes
/// belong to the engagement's story and land in its audit trail. The default
/// is an in-memory implementation; the durable PostgreSQL adapter swaps in
/// through the same replace-the-default shape the engagement repository uses.
/// </summary>
public interface IEngagementMembershipStore
{
    /// <summary>The member's record in an engagement, or null when the
    /// operator holds no membership there (the owner holds none either --
    /// ownership lives on the engagement itself).</summary>
    Task<EngagementMembership?> FindAsync(
        EngagementId engagementId,
        OperatorId operatorId,
        CancellationToken cancellationToken = default);

    /// <summary>Every membership in one engagement, ordered by the operator
    /// id, so the roster renders stably.</summary>
    Task<IReadOnlyList<EngagementMembership>> ListAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every membership one operator holds across all engagements -- the
    /// index the engagement listing filters by (owner or member).
    /// </summary>
    Task<IReadOnlyList<EngagementMembership>> ListForOperatorAsync(
        OperatorId operatorId,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces a membership. The caller owns the
    /// add-versus-role-change distinction (the store is an upsert); adding an
    /// existing member replaces the role, which is the assignment shape.</summary>
    Task SaveAsync(EngagementMembership membership, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a membership. Idempotent: removing an operator who holds none
    /// succeeds. Returns false when there was nothing to remove, so the caller
    /// can tell a removal from a no-op for the audit trail.
    /// </summary>
    Task<bool> RemoveAsync(
        EngagementId engagementId,
        OperatorId operatorId,
        CancellationToken cancellationToken = default);
}
