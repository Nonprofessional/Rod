using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Rod.CoreState.Engagements;

namespace Rod.CoreState.Deployment;

/// <summary>
/// In-memory <see cref="IDeployTokenService"/> by default. Mints a
/// 32-byte crypto-random secret, returns the base64url plaintext once, and keeps
/// only its SHA-256 hash so redeem can verify without ever storing the clear
/// secret. Redeem checks the hash, refuses expired, spent, or revoked tokens,
/// and consumes one use on success; a zero max-uses budget is unlimited and
/// never consumes.
/// </summary>
/// <remarks>
/// A credential stays resolvable for its whole lifecycle: a spent token is
/// kept at zero remaining uses and a revoked one is kept with its revocation
/// stamp, so a later attempt reads the honest reason (Spent, Revoked --
/// attribution on the exception) instead of dissolving into Unknown. Only a
/// hard delete (a launcher row going, <see cref="DeleteAsync"/>) removes the
/// resolution itself -- after that the secret belongs to no engagement and
/// its attempts leave no record.
/// </remarks>
public sealed class InMemoryDeployTokenService : IDeployTokenService
{
    private readonly IEngagementRepository _engagements;
    private readonly ConcurrentDictionary<DeployTokenId, StoredToken> _stored = new();
    private readonly Lock _redeemLock = new();

    // Defaults that suit tests and dev runs; production mints scoped values per request.
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);
    private const int DefaultMaxUses = 1;

    public InMemoryDeployTokenService(IEngagementRepository engagements)
        => _engagements = engagements;

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

        _stored[id] = new StoredToken(
            SHA256.HashData(secretBytes), engagementId, issuedBy, issuedAt, expiresAt,
            effectiveMaxUses, effectiveMaxUses, RevokedAt: null);

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

    public Task<RedeemedDeployToken> RedeemAsync(
        string secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Check-then-consume must be atomic: without the lock two concurrent
        // redeems of a single-use token could both pass the remaining-uses check.
        lock (_redeemLock)
        {
            var entry = FindForRead(secret, now);

            // An unlimited budget (max uses 0) never spends down, and a spent
            // budgeted token stays stored at zero -- the row is the refusal's
            // attribution, not housekeeping to collect.
            if (entry.Token.MaxUses != 0)
            {
                var remaining = entry.Token.RemainingUses - 1;
                _stored[entry.Id] = entry.Token with { RemainingUses = Math.Max(remaining, 0) };
            }

            return Task.FromResult(new RedeemedDeployToken
            {
                Id = entry.Id,
                EngagementId = entry.Token.EngagementId,
                IssuedBy = entry.Token.IssuedBy,
            });
        }
    }

    public Task<RedeemedDeployToken> VerifyAsync(
        string secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // The same checks redeem runs, minus the consume: the non-consuming
        // resolution every refusal path takes (architecture.md Sec 6). The
        // lock keeps the read off a concurrent redeem's check-then-consume
        // window.
        lock (_redeemLock)
        {
            var entry = FindForRead(secret, now);
            return Task.FromResult(new RedeemedDeployToken
            {
                Id = entry.Id,
                EngagementId = entry.Token.EngagementId,
                IssuedBy = entry.Token.IssuedBy,
            });
        }
    }

    // The shared lookup both redeem and verify run: hash the presented secret,
    // match it against the stored digests, and apply the expiry,
    // remaining-uses, and revocation refusals. Returns the matched id and its
    // stored token. Every refusal except a hash miss carries the matched
    // token's attribution on the exception.
    private (DeployTokenId Id, StoredToken Token) FindForRead(string secret, DateTimeOffset now)
    {
        // The plaintext is never stored, so we hash the presented secret and look
        // it up by digest. A bad format simply yields no match -> Unknown.
        byte[] presentedHash;
        try
        {
            presentedHash = SHA256.HashData(Base64Url.Decode(secret));
        }
        catch (FormatException)
        {
            throw new DeployTokenRedeemException(DeployTokenRedeemReason.Unknown, "Deploy token is malformed.");
        }

        var entryId = _stored.FirstOrDefault(kv => kv.Value.Hash.SequenceEqual(presentedHash)).Key;
        if (entryId == default)
            throw new DeployTokenRedeemException(
                DeployTokenRedeemReason.Unknown, "Deploy token is unknown.");

        var entry = _stored[entryId];
        if (entry.RevokedAt is { } revoked)
            throw new DeployTokenRedeemException(
                DeployTokenRedeemReason.Revoked, "Deploy token was revoked.",
                entry.EngagementId, entryId);
        if (now > entry.ExpiresAt)
            throw new DeployTokenRedeemException(
                DeployTokenRedeemReason.Expired, "Deploy token has expired.",
                entry.EngagementId, entryId);
        // The spent refusal is a budgeted token's condition: an unlimited
        // budget keeps RemainingUses at 0 as "not counted", never "spent".
        if (entry.MaxUses != 0 && entry.RemainingUses <= 0)
            throw new DeployTokenRedeemException(
                DeployTokenRedeemReason.Spent, "Deploy token has no remaining uses.",
                entry.EngagementId, entryId);

        return (entryId, entry);
    }

    public Task<bool> RevokeAsync(DeployTokenId id, CancellationToken cancellationToken = default)
    {
        // The soft kill: the row stays resolvable with its revocation stamp, so
        // later attempts on the credential read Revoked with attribution
        // instead of Unknown. True only on the flip -- an already-revoked row
        // answers false, the same honesty an absent one does, so a repeat
        // revoke neither reads as success nor double-writes the trail.
        lock (_redeemLock)
        {
            if (!_stored.TryGetValue(id, out var stored) || stored.RevokedAt is not null)
                return Task.FromResult(false);
            _stored[id] = stored with { RevokedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(DeployTokenId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_stored.TryRemove(id, out _));

    public Task<DeployTokenState?> FindAsync(DeployTokenId id, CancellationToken cancellationToken = default)
    {
        // A revoked credential reads as gone to the listings (the launcher row
        // carries its own revocation stamp); a spent one stays visible at zero
        // so the budget's ledger keeps its last page.
        if (!_stored.TryGetValue(id, out var stored) || stored.RevokedAt is not null)
            return Task.FromResult<DeployTokenState?>(null);
        return Task.FromResult<DeployTokenState?>(new DeployTokenState
        {
            Id = id,
            EngagementId = stored.EngagementId,
            IssuedBy = stored.IssuedBy,
            IssuedAt = stored.IssuedAt,
            ExpiresAt = stored.ExpiresAt,
            MaxUses = stored.MaxUses,
            RemainingUses = stored.RemainingUses,
        });
    }

    // IssuedBy is retained so redeem can attribute the deployment that follows:
    // a deploy token is redeemed by an implant, but the operator who minted it
    // authorized the deployment, and enrollment records that operator on the
    // implant (architecture.md Sec 11). MaxUses rides beside RemainingUses so
    // the inspectable state can state the budget, not just what is left of it.
    private sealed record StoredToken(
        byte[] Hash,
        EngagementId EngagementId,
        OperatorId IssuedBy,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        int MaxUses,
        int RemainingUses,
        DateTimeOffset? RevokedAt);
}
