using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Operators;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IOperatorRepository"/> (ADR 0003). Each call
/// creates a short-lived <see cref="RodPersistenceDbContext"/> from the factory,
/// performs its work, and disposes the context; the context is never held across
/// calls so the singleton adapter stays safe for concurrent use. Operators are
/// keyed by their typed id (mapped to a uuid column).
/// </summary>
internal sealed class PostgresOperatorRepository : IOperatorRepository
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresOperatorRepository(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task<Operator?> FindAsync(OperatorId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Operators.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    public async Task<Operator?> FindByHandleAsync(string handle, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // Handles are unique by provisioning (one operator per handle), so this
        // resolves to zero or one row; FirstOrDefaultAsync translates to a
        // limit-by-one equality probe on the handle column.
        return await db.Operators.AsNoTracking().FirstOrDefaultAsync(o => o.Handle == handle, cancellationToken);
    }

    public async Task<Operator> GetOrThrowAsync(OperatorId id, CancellationToken cancellationToken = default)
        => await FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Operator {id} does not exist.");

    public async Task SaveAsync(Operator @operator, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        // Insert-or-replace by id: the in-memory adapter's TryAdd/overwrite
        // behavior. Track the incoming entity and let the store decide insert vs
        // update based on whether the key already exists.
        var existing = await db.Operators.FindAsync(new object?[] { @operator.Id }, cancellationToken);
        if (existing is null)
            db.Operators.Add(@operator);
        else
            db.Entry(existing).CurrentValues.SetValues(@operator);

        await db.SaveChangesAsync(cancellationToken);
    }
}
