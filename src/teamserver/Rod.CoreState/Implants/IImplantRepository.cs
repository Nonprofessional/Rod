using Rod.CoreState.Engagements;

namespace Rod.CoreState.Implants;

/// <summary>
/// Persistence port for <see cref="Implant"/> aggregates. The default
/// is an in-memory implementation; a PostgreSQL-backed adapter
/// arrives later without changing this contract.
/// </summary>
public interface IImplantRepository
{
    Task<Implant?> FindAsync(ImplantId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// All implants enrolled into an engagement, oldest first. Scoped by
    /// engagement so cross-engagement access never reaches this with another
    /// engagement's id.
    /// </summary>
    Task<IReadOnlyList<Implant>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(Implant implant, CancellationToken cancellationToken = default);
}
