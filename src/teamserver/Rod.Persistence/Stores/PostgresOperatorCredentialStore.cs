using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Operators;
using Rod.Persistence.Configurations;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IOperatorCredentialStore"/> (ADR 0003). Each call
/// creates a short-lived <see cref="RodPersistenceDbContext"/> from the factory,
/// performs its work, and disposes the context; the context is never held across
/// calls so the singleton adapter stays safe for concurrent use. Stores only the
/// opaque password hash the auth layer's password hasher produced -- never a
/// plaintext password -- keyed by operator id. This is the durable analogue of
/// <see cref="InMemoryOperatorCredentialStore"/>; the port keeps callers agnostic
/// to that, so the bootstrap seed and login path are unchanged when this adapter
/// is swapped in.
/// </summary>
internal sealed class PostgresOperatorCredentialStore : IOperatorCredentialStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresOperatorCredentialStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task<string?> FindHashAsync(OperatorId operatorId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var credential = await db.OperatorCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OperatorId == operatorId, cancellationToken);
        return credential?.PasswordHash;
    }

    public async Task SetHashAsync(OperatorId operatorId, string passwordHash, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        // Insert-or-replace by operator id: one credential row per operator
        // (the operator_id primary key). Touching the hash also refreshes
        // updated_at so a password change is timestamped in the store.
        var existing = await db.OperatorCredentials.FindAsync(new object?[] { operatorId }, cancellationToken);
        if (existing is null)
        {
            db.OperatorCredentials.Add(new StoredOperatorCredential
            {
                OperatorId = operatorId,
                PasswordHash = passwordHash,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.PasswordHash = passwordHash;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeAsync(OperatorId operatorId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        // Deletes the verifier row; login reads the hash fresh per attempt, so
        // the revocation takes effect on the next login with no restart.
        var credential = await db.OperatorCredentials.FindAsync(new object?[] { operatorId }, cancellationToken);
        if (credential is not null)
        {
            db.OperatorCredentials.Remove(credential);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
