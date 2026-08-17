using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Staging;
using Rod.Persistence.Configurations;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IStagerTokenService"/> (ADR 0003). Mints a
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
/// <see cref="StagerTokenRedeemReason.Spent"/> instead of
/// <see cref="StagerTokenRedeemReason.Unknown"/> and the spent row stays in the
/// store for auditing. This is the deliberate durable analogue.
/// </para>
/// </remarks>
internal sealed class PostgresStagerTokenService : IStagerTokenService
{
    // Defaults carried verbatim from the in-memory service; they become
    // per-request inputs later.
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);
    private const int DefaultMaxUses = 1;

    private readonly IEngagementRepository _engagements;
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresStagerTokenService(
        IEngagementRepository engagements,
        IDbContextFactory<RodPersistenceDbContext> factory)
    {
        _engagements = engagements;
        _factory = factory;
    }

    public async Task<StagerToken> MintAsync(
        EngagementId engagementId,
        OperatorId issuedBy,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default)
    {
        var engagement = await _engagements.FindAsync(engagementId, cancellationToken)
            ?? throw new StagerTokenException($"Engagement {engagementId} does not exist.");

        if (engagement.OwnerId != issuedBy)
            throw new StagerTokenException(
                $"Operator {issuedBy} is not the owner of engagement {engagementId} and cannot mint stager tokens for it.");

        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var expiresAt = issuedAt + DefaultLifetime;
        var id = StagerTokenId.New();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        db.StagerTokens.Add(new StoredStagerToken
        {
            Id = id,
            EngagementId = engagementId,
            IssuedBy = issuedBy,
            Hash = SHA256.HashData(secretBytes),
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
            MaxUses = DefaultMaxUses,
            RemainingUses = DefaultMaxUses,
        });
        await db.SaveChangesAsync(cancellationToken);

        return new StagerToken
        {
            Id = id,
            EngagementId = engagementId,
            Secret = Base64Url(secretBytes),
            IssuedBy = issuedBy,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
            MaxUses = DefaultMaxUses,
        };
    }

    public async Task<RedeemedStagerToken> RedeemAsync(
        string secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // The plaintext is never stored, so hash the presented secret and look it
        // up by digest. A bad format yields no match -> Unknown.
        byte[] presentedHash;
        try
        {
            presentedHash = SHA256.HashData(FromBase64Url(secret));
        }
        catch (FormatException)
        {
            throw new StagerTokenRedeemException(StagerTokenRedeemReason.Unknown, "Stager token is malformed.");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Read the row by digest for the result and the refusal reason. The
        // consumed columns (Id, EngagementId, IssuedBy) are immutable, so reading
        // them before the consume is safe.
        var entry = await db.StagerTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Hash == presentedHash, cancellationToken);
        if (entry is null)
            throw new StagerTokenRedeemException(StagerTokenRedeemReason.Unknown, "Stager token is unknown.");

        // Atomic check-then-consume: the UPDATE decrements remaining_uses only
        // when every precondition holds, so two concurrent redeems of a
        // single-use token cannot both succeed. rowsAffected tells consume vs.
        // refusal; the prior read distinguishes Expired from Spent.
        var rowsAffected = await db.StagerTokens
            .Where(t => t.Hash == presentedHash && now <= t.ExpiresAt && t.RemainingUses > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RemainingUses, t => t.RemainingUses - 1), cancellationToken);

        if (rowsAffected == 1)
        {
            await tx.CommitAsync(cancellationToken);
            return new RedeemedStagerToken
            {
                Id = entry.Id,
                EngagementId = entry.EngagementId,
                IssuedBy = entry.IssuedBy,
            };
        }

        // The row exists but the conditional UPDATE matched nothing: either the
        // token has passed its expiry or it has no uses left. The order matches
        // the in-memory service (Expired before Spent).
        throw now > entry.ExpiresAt
            ? new StagerTokenRedeemException(StagerTokenRedeemReason.Expired, "Stager token has expired.")
            : new StagerTokenRedeemException(StagerTokenRedeemReason.Spent, "Stager token has no remaining uses.");
    }

    public async Task<RedeemedStagerToken> VerifyAsync(
        string secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // The same checks redeem runs, minus the consume: a stage-1 stager's
        // payload fetch must leave the token whole for the stage-2's enroll
        // (architecture.md Sec 6). A plain read suffices -- nothing mutates, so
        // no transaction and no conditional UPDATE are needed.
        byte[] presentedHash;
        try
        {
            presentedHash = SHA256.HashData(FromBase64Url(secret));
        }
        catch (FormatException)
        {
            throw new StagerTokenRedeemException(StagerTokenRedeemReason.Unknown, "Stager token is malformed.");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var entry = await db.StagerTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Hash == presentedHash, cancellationToken);
        if (entry is null)
            throw new StagerTokenRedeemException(StagerTokenRedeemReason.Unknown, "Stager token is unknown.");
        if (now > entry.ExpiresAt)
            throw new StagerTokenRedeemException(StagerTokenRedeemReason.Expired, "Stager token has expired.");
        if (entry.RemainingUses <= 0)
            throw new StagerTokenRedeemException(StagerTokenRedeemReason.Spent, "Stager token has no remaining uses.");

        return new RedeemedStagerToken
        {
            Id = entry.Id,
            EngagementId = entry.EngagementId,
            IssuedBy = entry.IssuedBy,
        };
    }

    // RFC 4648 base64url without padding -- URL-safe for transport.
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    // Inverse of Base64Url: re-add padding the decoder requires.
    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("Invalid base64url length."),
        };
        return Convert.FromBase64String(padded);
    }
}
