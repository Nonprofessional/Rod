using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Rod.CoreState.Engagements;

namespace Rod.CoreState.Staging;

/// <summary>
/// In-memory <see cref="IStagerTokenService"/> by default. Mints a
/// 32-byte crypto-random secret, returns the base64url plaintext once, and keeps
/// only its SHA-256 hash so redeem can verify without ever storing the clear
/// secret. Redeem checks the hash, refuses expired or spent tokens, and consumes
/// one use on success; a zero max-uses budget is unlimited and never consumes.
/// </summary>
public sealed class InMemoryStagerTokenService : IStagerTokenService
{
    private readonly IEngagementRepository _engagements;
    private readonly ConcurrentDictionary<StagerTokenId, StoredToken> _stored = new();
    private readonly Lock _redeemLock = new();

    // Defaults that suit tests and dev runs; production mints scoped values per request.
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);
    private const int DefaultMaxUses = 1;

    public InMemoryStagerTokenService(IEngagementRepository engagements)
        => _engagements = engagements;

    public async Task<StagerToken> MintAsync(
        EngagementId engagementId,
        OperatorId issuedBy,
        DateTimeOffset issuedAt,
        int? maxUses = null,
        TimeSpan? lifetime = null,
        ShellSessionId? originShellSession = null,
        CancellationToken cancellationToken = default)
    {
        var engagement = await _engagements.FindAsync(engagementId, cancellationToken)
            ?? throw new StagerTokenException($"Engagement {engagementId} does not exist.");

        if (engagement.OwnerId != issuedBy)
            throw new StagerTokenException(
                $"Operator {issuedBy} is not the owner of engagement {engagementId} and cannot mint stager tokens for it.");

        var effectiveMaxUses = maxUses ?? DefaultMaxUses;
        var expiresAt = issuedAt + (lifetime ?? DefaultLifetime);
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var id = StagerTokenId.New();

        _stored[id] = new StoredToken(
            SHA256.HashData(secretBytes), engagementId, issuedBy, issuedAt, expiresAt, effectiveMaxUses, effectiveMaxUses, originShellSession);

        return new StagerToken
        {
            Id = id,
            EngagementId = engagementId,
            Secret = Base64Url.Encode(secretBytes),
            IssuedBy = issuedBy,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
            MaxUses = effectiveMaxUses,
            OriginShellSession = originShellSession,
        };
    }

    public Task<RedeemedStagerToken> RedeemAsync(
        string secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Check-then-consume must be atomic: without the lock two concurrent
        // redeems of a single-use token could both pass the remaining-uses check.
        lock (_redeemLock)
        {
            var entry = FindForRead(secret, now);

            // An unlimited budget (max uses 0) never spends down: the row stays
            // whole for every redeem until it expires or is revoked.
            if (entry.Token.MaxUses != 0)
            {
                var remaining = entry.Token.RemainingUses - 1;
                if (remaining <= 0)
                    _stored.TryRemove(entry.Id, out _);
                else
                    _stored[entry.Id] = entry.Token with { RemainingUses = remaining };
            }

            return Task.FromResult(new RedeemedStagerToken
            {
                Id = entry.Id,
                EngagementId = entry.Token.EngagementId,
                IssuedBy = entry.Token.IssuedBy,
                OriginShellSession = entry.Token.OriginShellSession,
            });
        }
    }

    public Task<RedeemedStagerToken> VerifyAsync(
        string secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // The same checks redeem runs, minus the consume: a stage-1 stager's
        // payload fetch must leave the token whole for the stage-2's enroll
        // (architecture.md Sec 6). The lock keeps the read off a concurrent
        // redeem's check-then-consume window.
        lock (_redeemLock)
        {
            var entry = FindForRead(secret, now);
            return Task.FromResult(new RedeemedStagerToken
            {
                Id = entry.Id,
                EngagementId = entry.Token.EngagementId,
                IssuedBy = entry.Token.IssuedBy,
                OriginShellSession = entry.Token.OriginShellSession,
            });
        }
    }

    // The shared lookup both redeem and verify run: hash the presented secret,
    // match it against the stored digests, and apply the expiry and
    // remaining-uses refusals. Returns the matched id and its stored token.
    private (StagerTokenId Id, StoredToken Token) FindForRead(string secret, DateTimeOffset now)
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
            throw new StagerTokenRedeemException(StagerTokenRedeemReason.Unknown, "Stager token is malformed.");
        }

        var entryId = _stored.FirstOrDefault(kv => kv.Value.Hash.SequenceEqual(presentedHash)).Key;
        if (entryId == default)
            throw new StagerTokenRedeemException(
                StagerTokenRedeemReason.Unknown, "Stager token is unknown.");

        var entry = _stored[entryId];
        if (now > entry.ExpiresAt)
            throw new StagerTokenRedeemException(
                StagerTokenRedeemReason.Expired, "Stager token has expired.");
        // The spent refusal is a budgeted token's condition: an unlimited
        // budget keeps RemainingUses at 0 as "not counted", never "spent".
        if (entry.MaxUses != 0 && entry.RemainingUses <= 0)
            throw new StagerTokenRedeemException(
                StagerTokenRedeemReason.Spent, "Stager token has no remaining uses.");

        return (entryId, entry);
    }

    public Task<bool> RevokeAsync(StagerTokenId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_stored.TryRemove(id, out _));

    public Task<StagerTokenState?> FindAsync(StagerTokenId id, CancellationToken cancellationToken = default)
    {
        // A token this store spent or revoked is gone, so the read reports
        // nothing -- the payload library renders that as "no enrolls left."
        if (!_stored.TryGetValue(id, out var stored))
            return Task.FromResult<StagerTokenState?>(null);
        return Task.FromResult<StagerTokenState?>(new StagerTokenState
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
    // a stager token is redeemed by an implant, but the operator who minted it
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
        ShellSessionId? OriginShellSession = null);
}
