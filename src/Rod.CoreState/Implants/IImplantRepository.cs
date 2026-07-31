namespace Rod.CoreState.Implants;

/// <summary>
/// Persistence port for <see cref="Implant"/> aggregates. The walking skeleton
/// (roadmap M1) ships an in-memory implementation; a PostgreSQL-backed adapter
/// arrives later without changing this contract.
/// </summary>
public interface IImplantRepository
{
    Task<Implant?> FindAsync(ImplantId id, CancellationToken cancellationToken = default);

    Task<Implant> GetOrThrowAsync(ImplantId id, CancellationToken cancellationToken = default);

    Task SaveAsync(Implant implant, CancellationToken cancellationToken = default);
}
