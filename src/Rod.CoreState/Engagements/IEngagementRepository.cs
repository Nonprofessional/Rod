namespace Rod.CoreState.Engagements;

/// <summary>
/// Persistence port for <see cref="Engagement"/> aggregates. The walking
/// skeleton (roadmap M1) ships an in-memory implementation; a PostgreSQL-backed
/// adapter arrives later without changing this contract.
/// </summary>
public interface IEngagementRepository
{
    Task<Engagement?> FindAsync(EngagementId id, CancellationToken cancellationToken = default);

    Task<Engagement> GetOrThrowAsync(EngagementId id, CancellationToken cancellationToken = default);

    /// <summary>All engagements, oldest first (roadmap M1.5).</summary>
    Task<IReadOnlyList<Engagement>> ListAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(Engagement engagement, CancellationToken cancellationToken = default);
}
