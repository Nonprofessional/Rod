namespace Rod.CoreState.Operators;

/// <summary>
/// Persistence port for <see cref="Operator"/> aggregates. The walking skeleton
/// () ships an in-memory implementation; a PostgreSQL-backed adapter
/// arrives later without changing this contract.
/// </summary>
public interface IOperatorRepository
{
    Task<Operator?> FindAsync(OperatorId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds an operator by handle. Operator login resolves the account by handle
    /// before the auth layer verifies its password, so the repository can answer
    /// that lookup the same way it answers a by-id lookup. Returns null when no
    /// operator owns the handle. Handles are expected to be unique -- the
    /// provisioning path (bootstrap seed and future management) creates one
    /// operator per handle -- and the match is exact and case-sensitive.
    /// </summary>
    Task<Operator?> FindByHandleAsync(string handle, CancellationToken cancellationToken = default);

    Task<Operator> GetOrThrowAsync(OperatorId id, CancellationToken cancellationToken = default);

    Task SaveAsync(Operator @operator, CancellationToken cancellationToken = default);
}
