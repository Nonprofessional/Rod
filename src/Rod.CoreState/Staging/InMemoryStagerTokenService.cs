using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Rod.CoreState.Engagements;

namespace Rod.CoreState.Staging;

/// <summary>
/// In-memory <see cref="IStagerTokenService"/> for the walking skeleton. Mints a
/// 32-byte crypto-random secret, returns the base64url plaintext once, and keeps
/// only its SHA-256 hash so a later redeem step (M1.2) can verify without ever
/// storing the clear secret.
/// </summary>
public sealed class InMemoryStagerTokenService : IStagerTokenService
{
    private readonly IEngagementRepository _engagements;
    private readonly ConcurrentDictionary<StagerTokenId, (byte[] Hash, EngagementId EngagementId, DateTimeOffset ExpiresAt, int MaxUses)> _stored = new();

    // Sensible defaults for the skeleton; these become per-request inputs later.
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);
    private const int DefaultMaxUses = 1;

    public InMemoryStagerTokenService(IEngagementRepository engagements)
        => _engagements = engagements;

    public async Task<StagerToken> MintAsync(
        EngagementId engagementId,
        OperatorId issuedBy,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default)
    {
        var engagement = await _engagements.FindAsync(engagementId, cancellationToken)
            ?? throw new StagerTokenException($"Engagement {engagementId} does not exist.");

        if (!engagement.HasMember(issuedBy))
            throw new StagerTokenException(
                $"Operator {issuedBy} is not a member of engagement {engagementId} and cannot mint stager tokens for it.");

        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var expiresAt = issuedAt + DefaultLifetime;
        var id = StagerTokenId.New();

        _stored[id] = (SHA256.HashData(secretBytes), engagementId, expiresAt, DefaultMaxUses);

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

    // RFC 4648 base64url without padding -- URL-safe for transport.
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
