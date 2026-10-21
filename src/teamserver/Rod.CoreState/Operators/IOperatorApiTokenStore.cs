using System.Collections.Concurrent;
using System.Security.Cryptography;
using Rod.CoreState;

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
/// stored afterwards -- the same hash-only rule the deploy-token and password
/// stores keep. The scope names the capability floor the token carries
/// (ADR-006): it rides the row, not the secret.
/// </summary>
public sealed record MintedOperatorApiToken(
    OperatorApiTokenId TokenId,
    OperatorId OperatorId,
    OperatorTokenScope Scope,
    string Secret,
    DateTimeOffset CreatedAt);

/// <summary>
/// A minted token as a listing row: identity, scope, and lifetime, never the
/// secret.
/// </summary>
public sealed record OperatorApiTokenRecord(
    OperatorApiTokenId TokenId,
    OperatorId OperatorId,
    OperatorTokenScope Scope,
    DateTimeOffset CreatedAt);

/// <summary>
/// What a presented token secret resolves to: the operator it authenticates
/// and the scope that caps what the token may do.
/// </summary>
public sealed record ResolvedOperatorApiToken(
    OperatorId OperatorId,
    OperatorTokenScope Scope);

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
    /// Mints a token for an operator at the named scope and returns it with
    /// the plaintext secret (shown exactly once). The digest is what persists.
    /// </summary>
    Task<MintedOperatorApiToken> MintAsync(
        OperatorId operatorId,
        OperatorTokenScope scope,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the operator and scope a presented secret belongs to, or null
    /// when the token is unknown or revoked. Read fresh on every attempt -- the
    /// revocation shape the credential store keeps.
    /// </summary>
    Task<ResolvedOperatorApiToken?> FindOperatorAsync(
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

    private sealed record Stored(OperatorId OperatorId, OperatorTokenScope Scope, byte[] Hash, DateTimeOffset CreatedAt);

    public Task<MintedOperatorApiToken> MintAsync(
        OperatorId operatorId,
        OperatorTokenScope scope,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var (secret, digest) = SecretDigest.Mint();
        var tokenId = OperatorApiTokenId.New();
        _tokens[tokenId] = new Stored(operatorId, scope, digest, at);
        return Task.FromResult(new MintedOperatorApiToken(tokenId, operatorId, scope, secret, at));
    }

    public Task<ResolvedOperatorApiToken?> FindOperatorAsync(
        string secret,
        CancellationToken cancellationToken = default)
    {
        var digest = SecretDigest.DigestOf(secret);
        if (digest is null)
            return Task.FromResult<ResolvedOperatorApiToken?>(null); // malformed never reaches the lookup

        foreach (var stored in _tokens.Values)
        {
            if (CryptographicOperations.FixedTimeEquals(digest, stored.Hash))
                return Task.FromResult<ResolvedOperatorApiToken?>(new ResolvedOperatorApiToken(stored.OperatorId, stored.Scope));
        }
        return Task.FromResult<ResolvedOperatorApiToken?>(null);
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
            .Select(pair => new OperatorApiTokenRecord(pair.Key, operatorId, pair.Value.Scope, pair.Value.CreatedAt))
            .ToArray();
        return Task.FromResult<IReadOnlyList<OperatorApiTokenRecord>>(rows);
    }
}
