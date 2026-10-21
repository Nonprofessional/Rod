using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Operators;
using Rod.Persistence.Configurations;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IOperatorApiTokenStore"/>. Each call creates a
/// short-lived context from the factory, the same shape the other durable
/// adapters keep; a presented secret resolves through the unique digest index,
/// and revocation deletes the row.
/// </summary>
internal sealed class PostgresOperatorApiTokenStore : IOperatorApiTokenStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresOperatorApiTokenStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task<MintedOperatorApiToken> MintAsync(
        OperatorId operatorId,
        OperatorTokenScope scope,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        // The mint itself is the in-memory shape (fresh random bytes, digest
        // computed alongside); the row is what persists here.
        var (secret, digest) = SecretDigest.Mint();
        var tokenId = OperatorApiTokenId.New();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        db.Set<StoredOperatorApiToken>().Add(new StoredOperatorApiToken
        {
            TokenId = tokenId,
            OperatorId = operatorId,
            Hash = digest,
            Scope = OperatorTokenScopes.ToName(scope),
            CreatedAt = at,
        });
        await db.SaveChangesAsync(cancellationToken);

        return new MintedOperatorApiToken(tokenId, operatorId, scope, secret, at);
    }

    public async Task<ResolvedOperatorApiToken?> FindOperatorAsync(
        string secret,
        CancellationToken cancellationToken = default)
    {
        var digest = SecretDigest.DigestOf(secret);
        if (digest is null)
            return null; // malformed never reaches the lookup

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<StoredOperatorApiToken>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Hash == digest, cancellationToken);
        if (row is null)
            return null;
        // An unknown scope name in an old row reads as the read floor -- the
        // same least-privilege default the column's database default keeps.
        if (!OperatorTokenScopes.TryFromName(row.Scope, out var scope))
            scope = OperatorTokenScope.Read;
        return new ResolvedOperatorApiToken(row.OperatorId, scope);
    }

    public async Task RevokeAsync(
        OperatorId operatorId,
        OperatorApiTokenId tokenId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<StoredOperatorApiToken>()
            .FirstOrDefaultAsync(t => t.TokenId == tokenId && t.OperatorId == operatorId, cancellationToken);
        if (row is not null)
        {
            db.Set<StoredOperatorApiToken>().Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<OperatorApiTokenRecord>> ListAsync(
        OperatorId operatorId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Set<StoredOperatorApiToken>().AsNoTracking()
            .Where(t => t.OperatorId == operatorId)
            .OrderBy(t => t.CreatedAt)
            .ToArrayAsync(cancellationToken);
        return rows.Select(r => new OperatorApiTokenRecord(
            r.TokenId,
            r.OperatorId,
            OperatorTokenScopes.TryFromName(r.Scope, out var scope) ? scope : OperatorTokenScope.Read,
            r.CreatedAt)).ToArray();
    }
}
