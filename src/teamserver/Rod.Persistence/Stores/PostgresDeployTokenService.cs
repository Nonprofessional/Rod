using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Deployment;
using Rod.Persistence.Configurations;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IDeployTokenService"/> (ADR 0003). Mints a
/// 32-byte crypto-random secret, returns the base64url plaintext exactly once,
/// and stores only its SHA-256 hash so the clear secret is never persisted.
/// Redeem is the entry point of enrollment: the presented plaintext is hashed,
/// the row is found by digest, and one use is consumed on success.
/// </summary>
/// <remarks>
/// <para>
/// The check-then-consume on redeem is the one place in  where the durable
/// store must guard against a real concurrency hazard: two concurrent redeems of
/// a single-use token could both pass a read-then-decrement sequence. It is done
/// with a single conditional <c>UPDATE</c> that puts every precondition (the
/// hash match, <c>now &lt;= expires_at</c>, <c>remaining_uses &gt; 0</c>) in the
/// <c>WHERE</c> clause and decrements in place, so Postgres's row-level locking
/// serializes the two attempts and at most one sees <c>rowsAffected == 1</c>.
/// No optimistic-concurrency token lives on the model; the atomic statement is
/// the guard, per ADR 0003.
/// </para>
/// <para>
/// A spent token is kept at <c>remaining_uses = 0</c> rather than deleted (the
/// in-memory service deletes at zero), so a later redeem attempt reads
/// <see cref="DeployTokenRedeemReason.Spent"/> instead of
/// <see cref="DeployTokenRedeemReason.Unknown"/> and the spent row stays in the
/// store for auditing. This is the deliberate durable analogue.
/// </para>
/// </remarks>
internal sealed class PostgresDeployTokenService : IDeployTokenService
{
    // The fall-back shape when a mint names no scope: single-use, one hour.
    // A batch mint passes explicit maxUses/lifetime through the service.
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);
    private const int DefaultMaxUses = 1;

    private readonly IEngagementRepository _engagements;
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresDeployTokenService(
        IEngagementRepository engagements,
        IDbContextFactory<RodPersistenceDbContext> factory)
    {
        _engagements = engagements;
        _factory = factory;
    }

    public async Task<DeployToken> MintAsync(
        EngagementId engagementId,
        OperatorId issuedBy,
        DateTimeOffset issuedAt,
        int? maxUses = null,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        var engagement = await _engagements.FindAsync(engagementId, cancellationToken)
            ?? throw new DeployTokenException($"Engagement {engagementId} does not exist.");

        if (engagement.OwnerId != issuedBy)
            throw new DeployTokenException(
                $"Operator {issuedBy} is not the owner of engagement {engagementId} and cannot mint deploy tokens for it.");

        var effectiveMaxUses = maxUses ?? DefaultMaxUses;
        var expiresAt = issuedAt + (lifetime ?? DefaultLifetime);
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var id = DeployTokenId.New();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        db.DeployTokens.Add(new StoredDeployToken
        {
            Id = id,
            EngagementId = engagementId,
            IssuedBy = issuedBy,
            Hash = SHA256.HashData(secretBytes),
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
            MaxUses = effectiveMaxUses,
            RemainingUses = effectiveMaxUses,
        });
        await db.SaveChangesAsync(cancellationToken);

        return new DeployToken
        {
            Id = id,
            EngagementId = engagementId,
            Secret = Base64Url.Encode(secretBytes),
            IssuedBy = issuedBy,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
            MaxUses = effectiveMaxUses,
        };
    }

    public async Task<RedeemedDeployToken> RedeemAsync(
        string secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // The plaintext is never stored, so hash the presented secret and look it
        // up by digest. A bad format yields no match -> Unknown.
        byte[] presentedHash;
        try
        {
            presentedHash = SHA256.HashData(Base64Url.Decode(secret));
        }
        catch (FormatException)
        {
            throw new DeployTokenRedeemException(DeployTokenRedeemReason.Unknown, "Deploy token is malformed.");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Read the row by digest for the result and the refusal reason. The
        // consumed columns (Id, EngagementId, IssuedBy) are immutable, so reading
        // them before the consume is safe.
        var entry = await db.DeployTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Hash == presentedHash, cancellationToken);
        if (entry is null)
            throw new DeployTokenRedeemException(DeployTokenRedeemReason.Unknown, "Deploy token is unknown.");

        // Atomic check-then-consume: the UPDATE matches only when every
        // precondition holds, so two concurrent redeems of a single-use token
        // cannot both succeed. An unlimited budget (max_uses 0) matches without
        // decrementing -- the row stays whole for every redeem until it expires
        // or is revoked. rowsAffected tells consume vs. refusal; the prior read
        // distinguishes Revoked, Expired, and Spent.
        var rowsAffected = await db.DeployTokens
            .Where(t => t.Hash == presentedHash
                && t.RevokedAt == null
                && now <= t.ExpiresAt
                && (t.MaxUses == 0 || t.RemainingUses > 0))
            .ExecuteUpdateAsync(
                s => s.SetProperty(
                    t => t.RemainingUses,
                    t => t.MaxUses == 0 ? t.RemainingUses : t.RemainingUses - 1),
                cancellationToken);

        if (rowsAffected == 1)
        {
            await tx.CommitAsync(cancellationToken);
            return new RedeemedDeployToken
            {
                Id = entry.Id,
                EngagementId = entry.EngagementId,
                IssuedBy = entry.IssuedBy,
            };
        }

        // The row exists but the conditional UPDATE matched nothing: revoked,
        // expired, or out of uses. The order matches the in-memory service
        // (Revoked, then Expired, then Spent), and every refusal carries the
        // matched row's attribution.
        throw Refusal(entry, now);
    }

    // Builds the refusal for a matched row, reason first and attribution
    // always aboard -- the durable twin of the in-memory service's ordering.
    private static DeployTokenRedeemException Refusal(StoredDeployToken entry, DateTimeOffset now)
        => entry.RevokedAt is not null
            ? new DeployTokenRedeemException(
                DeployTokenRedeemReason.Revoked, "Deploy token was revoked.",
                entry.EngagementId, entry.Id)
            : now > entry.ExpiresAt
                ? new DeployTokenRedeemException(
                    DeployTokenRedeemReason.Expired, "Deploy token has expired.",
                    entry.EngagementId, entry.Id)
                : new DeployTokenRedeemException(
                    DeployTokenRedeemReason.Spent, "Deploy token has no remaining uses.",
                    entry.EngagementId, entry.Id);

    public async Task<bool> RevokeAsync(DeployTokenId id, CancellationToken cancellationToken = default)
    {
        // The soft kill: stamp revoked_at and keep the row, so later attempts
        // read Revoked with their engagement attribution. True only on the
        // flip -- an already-revoked row answers false, the same honesty an
        // absent one does.
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.DeployTokens
            .Where(t => t.Id == id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow), cancellationToken);
        return rows == 1;
    }

    public async Task<bool> DeleteAsync(DeployTokenId id, CancellationToken cancellationToken = default)
    {
        // The hard kill that rides a launcher row's deletion: the resolution
        // itself goes, and the secret belongs to no engagement afterwards.
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.DeployTokens
            .Where(t => t.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
        return rows == 1;
    }

    public async Task<DeployTokenState?> FindAsync(DeployTokenId id, CancellationToken cancellationToken = default)
    {
        // A spent row survives here at remaining_uses = 0 (see the class
        // remarks), so the durable read reports the full budget even after the
        // token is spent; a revoked row reads as gone (the launcher row
        // carries its own revocation stamp).
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.DeployTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.RevokedAt == null, cancellationToken);
        if (stored is null)
            return null;
        return new DeployTokenState
        {
            Id = stored.Id,
            EngagementId = stored.EngagementId,
            IssuedBy = stored.IssuedBy,
            IssuedAt = stored.IssuedAt,
            ExpiresAt = stored.ExpiresAt,
            MaxUses = stored.MaxUses,
            RemainingUses = stored.RemainingUses,
        };
    }

    public async Task<RedeemedDeployToken> VerifyAsync(
        string secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // The same checks redeem runs, minus the consume: a downloader's
        // payload fetch must leave the credential whole for the artifact's
        // enroll (architecture.md Sec 6). A plain read suffices -- nothing
        // mutates, so no transaction and no conditional UPDATE are needed.
        byte[] presentedHash;
        try
        {
            presentedHash = SHA256.HashData(Base64Url.Decode(secret));
        }
        catch (FormatException)
        {
            throw new DeployTokenRedeemException(DeployTokenRedeemReason.Unknown, "Deploy token is malformed.");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var entry = await db.DeployTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Hash == presentedHash, cancellationToken);
        if (entry is null)
            throw new DeployTokenRedeemException(DeployTokenRedeemReason.Unknown, "Deploy token is unknown.");
        if (entry.RevokedAt is not null || now > entry.ExpiresAt || (entry.MaxUses != 0 && entry.RemainingUses <= 0))
            throw Refusal(entry, now);

        return new RedeemedDeployToken
        {
            Id = entry.Id,
            EngagementId = entry.EngagementId,
            IssuedBy = entry.IssuedBy,
        };
    }
}
