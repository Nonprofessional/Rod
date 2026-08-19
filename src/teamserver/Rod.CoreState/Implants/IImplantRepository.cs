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

    /// <summary>
    /// The Pivot-class children of <paramref name="parent"/> -- the implants
    /// its beacon stream fronts (architecture.md Sec 5.2, the Pivot class): a
    /// pivot child has no process of its own, so its tasking is claimed and
    /// executed by the parent's stream. Children of other classes run their
    /// own processes and are not fronted. Order is unspecified; the caller
    /// (the fronting dispatch claim) merges across targets by queue order.
    /// </summary>
    Task<IReadOnlyList<Implant>> ListFrontedPivotsAsync(
        ImplantId parent,
        CancellationToken cancellationToken = default);

    Task SaveAsync(Implant implant, CancellationToken cancellationToken = default);
}
