using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Rod.CoreState.Operators;

/// <summary>
/// Identifies a minted operator API token (architecture.md Sec 9 -- identity).
/// </summary>
public readonly record struct OperatorApiTokenId(Guid Value)
{
    public static OperatorApiTokenId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// A freshly minted API token: the id (for listing and revocation) and the
/// plaintext secret, shown exactly once at mint. Only the secret's digest is
/// stored afterwards -- the same hash-only rule the stager-token and password
/// stores keep.
/// </summary>
public sealed record MintedOperatorApiToken(
    OperatorApiTokenId TokenId,
    OperatorId OperatorId,
    string Secret,
    DateTimeOffset CreatedAt);

/// <summary>
/// A minted token as a listing row: identity and lifetime, never the secret.
/// </summary>
public sealed record OperatorApiTokenRecord(
    OperatorApiTokenId TokenId,
    OperatorId OperatorId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Persistence port for operator API tokens (architecture.md Sec 9 -- the
/// identity model's API tokens): bearer credentials minted per operator,
/// honored by the operator API alongside cookie sessions, and revocable like
/// credentials. Stores only the SHA-256 digest of each secret -- never the
/// plaintext -- so a leaked store does not leak working tokens; the secret is
/// shown exactly once, at mint.
/// </summary>
/// <remarks>
/// A token is independent of the operator's password: revoking the password
/// credential ends cookie sessions but leaves minted tokens working, and a
/// token is revoked by its own route. This is the standard separation --
/// rotation of one must not silently invalidate the other -- and each half
/// keeps the same immediate-effect, no-restart revocation shape.
/// </remarks>
public interface IOperatorApiTokenStore
{
    /// <summary>
    /// Mints a token for an operator and returns it with the plaintext secret
    /// (shown exactly once). The digest is what persists.
    /// </summary>
    Task<MintedOperatorApiToken> MintAsync(
        OperatorId operatorId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the operator a presented secret belongs to, or null when the
    /// token is unknown or revoked. Read fresh on every attempt -- the
    /// revocation shape the credential store keeps.
    /// </summary>
    Task<OperatorId?> FindOperatorAsync(
        string secret,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes one token (architecture.md Sec 9): deletes its digest, so the
    /// next request presenting it fails. Idempotent; revoking an unknown token
    /// succeeds.
    /// </summary>
    Task RevokeAsync(
        OperatorId operatorId,
        OperatorApiTokenId tokenId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The operator's minted tokens as listing rows (no secrets), oldest
    /// first.
    /// </summary>
    Task<IReadOnlyList<OperatorApiTokenRecord>> ListAsync(
        OperatorId operatorId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory <see cref="IOperatorApiTokenStore"/>: rows keyed by token id with
/// a digest lookup for presented secrets. State is lost on restart; the port
/// keeps callers agnostic to that, and the durable PostgreSQL adapter lives in
/// Rod.Persistence.
/// </summary>
public sealed class InMemoryOperatorApiTokenStore : IOperatorApiTokenStore
{
    private readonly ConcurrentDictionary<OperatorApiTokenId, Stored> _tokens = new();

    private sealed record Stored(OperatorId OperatorId, byte[] Hash, DateTimeOffset CreatedAt);

    public Task<MintedOperatorApiToken> MintAsync(
        OperatorId operatorId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Base64Url(secretBytes);
        var tokenId = OperatorApiTokenId.New();
        _tokens[tokenId] = new Stored(operatorId, SHA256.HashData(secretBytes), at);
        return Task.FromResult(new MintedOperatorApiToken(tokenId, operatorId, secret, at));
    }

    public Task<OperatorId?> FindOperatorAsync(
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
            return Task.FromResult<OperatorId?>(null); // malformed never reaches the lookup
        }

        var digest = SHA256.HashData(presented);
        foreach (var stored in _tokens.Values)
        {
            if (CryptographicOperations.FixedTimeEquals(digest, stored.Hash))
                return Task.FromResult<OperatorId?>(stored.OperatorId);
        }
        return Task.FromResult<OperatorId?>(null);
    }

    public Task RevokeAsync(
        OperatorId operatorId,
        OperatorApiTokenId tokenId,
        CancellationToken cancellationToken = default)
    {
        // Idempotent by shape: an unknown id removes nothing and succeeds,
        // the same rule credential revocation keeps.
        if (_tokens.TryGetValue(tokenId, out var stored) && stored.OperatorId == operatorId)
            _tokens.TryRemove(tokenId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OperatorApiTokenRecord>> ListAsync(
        OperatorId operatorId,
        CancellationToken cancellationToken = default)
    {
        var rows = _tokens
            .Where(pair => pair.Value.OperatorId == operatorId)
            .OrderBy(pair => pair.Value.CreatedAt)
            .Select(pair => new OperatorApiTokenRecord(pair.Key, operatorId, pair.Value.CreatedAt))
            .ToArray();
        return Task.FromResult<IReadOnlyList<OperatorApiTokenRecord>>(rows);
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
