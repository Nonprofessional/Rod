namespace Rod.CoreState.Operators;

/// <summary>
/// Persistence port for <see cref="Operator"/> aggregates. The walking skeleton
/// (roadmap M1) ships an in-memory implementation; a PostgreSQL-backed adapter
/// arrives later without changing this contract.
/// </summary>
public interface IOperatorRepository
{
    Task<Operator?> FindAsync(OperatorId id, CancellationToken cancellationToken = default);

    Task<Operator> GetOrThrowAsync(OperatorId id, CancellationToken cancellationToken = default);

    Task SaveAsync(Operator @operator, CancellationToken cancellationToken = default);
}
