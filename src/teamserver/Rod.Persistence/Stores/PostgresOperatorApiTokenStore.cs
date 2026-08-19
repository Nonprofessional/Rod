using System.Security.Cryptography;
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
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        // The mint itself is the in-memory shape (fresh random bytes, digest
        // computed alongside); the row is what persists here.
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Base64Url(secretBytes);
        var tokenId = OperatorApiTokenId.New();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        db.Set<StoredOperatorApiToken>().Add(new StoredOperatorApiToken
        {
            TokenId = tokenId,
            OperatorId = operatorId,
            Hash = SHA256.HashData(secretBytes),
            CreatedAt = at,
        });
        await db.SaveChangesAsync(cancellationToken);

        return new MintedOperatorApiToken(tokenId, operatorId, secret, at);
    }

    public async Task<OperatorId?> FindOperatorAsync(
        string secret,
        CancellationToken cancellationToken = default)
    {
        byte[] presented;
        try
        {
            presented = FromBase64Url(secret);
        }
        catch (FormatException)
        {
            return null; // malformed never reaches the lookup
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<StoredOperatorApiToken>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Hash == SHA256.HashData(presented), cancellationToken);
        return row?.OperatorId;
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
        return rows.Select(r => new OperatorApiTokenRecord(r.TokenId, r.OperatorId, r.CreatedAt)).ToArray();
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
