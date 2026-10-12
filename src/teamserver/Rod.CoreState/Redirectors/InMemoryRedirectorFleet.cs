using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Rod.CoreState.Redirectors;

/// <summary>
/// In-memory <see cref="IRedirectorFleet"/> default: the fleet lives with the
/// process, matching every other core-state store's in-memory posture. A
/// credential stays resolvable for its whole lifecycle -- spent and revoked
/// ones keep their stamps so a later attempt reads the honest reason instead
/// of dissolving into unknown.
/// </summary>
public sealed class InMemoryRedirectorFleet : IRedirectorFleet
{
    private readonly ConcurrentDictionary<RedirectorCredentialId, StoredCredential> _credentials = new();
    private readonly ConcurrentDictionary<RedirectorNodeId, RedirectorNode> _nodes = new();
    private readonly Lock _enrollLock = new();

    // A day between mint and enroll suits staged hardware; production mints
    // scoped values per request.
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(1);

    public Task<RedirectorCredential> MintCredentialAsync(
        string name,
        OperatorId issuedBy,
        DateTimeOffset issuedAt,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        var expiresAt = issuedAt + (lifetime ?? DefaultLifetime);
        var (secret, digest) = SecretDigest.Mint();
        var id = RedirectorCredentialId.New();

        _credentials[id] = new StoredCredential(
            digest, name, issuedBy, issuedAt, expiresAt, EnrolledNodeId: null, RevokedAt: null);

        return Task.FromResult(new RedirectorCredential
        {
            Id = id,
            Secret = secret,
            Name = name,
            IssuedBy = issuedBy,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
        });
    }

    public Task<IReadOnlyList<RedirectorCredentialState>> ListCredentialsAsync(
        CancellationToken cancellationToken = default)
    {
        var states = _credentials
            .OrderByDescending(kv => kv.Value.IssuedAt)
            .Select(kv => new RedirectorCredentialState
            {
                Id = kv.Key,
                Name = kv.Value.Name,
                IssuedBy = kv.Value.IssuedBy,
                IssuedAt = kv.Value.IssuedAt,
                ExpiresAt = kv.Value.ExpiresAt,
                EnrolledNodeId = kv.Value.EnrolledNodeId,
                RevokedAt = kv.Value.RevokedAt,
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<RedirectorCredentialState>>(states);
    }

    public Task RevokeCredentialAsync(
        RedirectorCredentialId id,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Idempotent, and a no-op on an unknown id: revoking a credential that
        // was never minted leaves nothing to enforce.
        if (_credentials.TryGetValue(id, out var credential))
            _credentials[id] = credential with { RevokedAt = credential.RevokedAt ?? now };
        return Task.CompletedTask;
    }

    public Task<EnrolledRedirector> EnrollAsync(
        string credentialSecret,
        string? remoteAddress,
        string? version,
        string? commit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Check-then-consume must be atomic: without the lock two concurrent
        // presentations of a single-use credential could both enroll.
        lock (_enrollLock)
        {
            var match = FindCredential(credentialSecret);
            if (match is null)
                throw new RedirectorEnrollException(
                    RedirectorEnrollReason.Unknown, "The presented credential matches no minted credential.");
            var (id, credential) = match.Value;

            if (credential.RevokedAt is not null)
                throw new RedirectorEnrollException(
                    RedirectorEnrollReason.Revoked, $"Credential {id} has been revoked.");
            if (credential.ExpiresAt <= now)
                throw new RedirectorEnrollException(
                    RedirectorEnrollReason.Expired, $"Credential {id} has passed its expiry.");
            if (credential.EnrolledNodeId is not null)
                throw new RedirectorEnrollException(
                    RedirectorEnrollReason.Spent, $"Credential {id} has already enrolled node {credential.EnrolledNodeId}.");

            var (nodeSecret, nodeDigest) = SecretDigest.Mint();
            var nodeId = RedirectorNodeId.New();
            var node = new RedirectorNode
            {
                Id = nodeId,
                Name = credential.Name,
                EnrolledBy = credential.IssuedBy,
                EnrolledAt = now,
                LastSeenAt = now,
                LastAddress = remoteAddress,
                ReportedVersion = version,
                ReportedCommit = commit,
                SecretDigest = Convert.ToHexString(nodeDigest),
            };
            _nodes[nodeId] = node;
            _credentials[id] = credential with { EnrolledNodeId = nodeId };

            return Task.FromResult(new EnrolledRedirector { Node = node, NodeSecret = nodeSecret });
        }
    }

    public Task<RedirectorNode> AuthenticateAsync(
        RedirectorNodeId id,
        string nodeSecret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!_nodes.TryGetValue(id, out var node)
            || !SecretDigestComparer.Same(node.SecretDigest, nodeSecret))
            // A wrong secret reads as unknown: the reconnect path gets no
            // oracle beyond "not yours".
            throw new RedirectorNodeRefusedException($"Node {id} is unknown or its secret is wrong.");
        if (node.RetiredAt is not null)
            throw new RedirectorNodeRefusedException($"Node {id} has been retired.");

        _nodes[id] = node with { LastSeenAt = now };
        return Task.FromResult(_nodes[id]);
    }

    public Task TouchAsync(
        RedirectorNodeId id,
        string? remoteAddress,
        string? version,
        string? commit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // A touch racing a retire (or a removed node) is not an error worth a
        // round trip.
        if (_nodes.TryGetValue(id, out var node))
        {
            _nodes[id] = node with
            {
                LastSeenAt = now,
                LastAddress = remoteAddress ?? node.LastAddress,
                ReportedVersion = version ?? node.ReportedVersion,
                ReportedCommit = commit ?? node.ReportedCommit,
            };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RedirectorNode>> ListNodesAsync(CancellationToken cancellationToken = default)
    {
        var nodes = _nodes.Values.OrderByDescending(n => n.EnrolledAt).ToList();
        return Task.FromResult<IReadOnlyList<RedirectorNode>>(nodes);
    }

    public Task<RedirectorNode?> FindAsync(RedirectorNodeId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_nodes.TryGetValue(id, out var node) ? node : null);

    public Task RetireAsync(RedirectorNodeId id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (_nodes.TryGetValue(id, out var node) && node.RetiredAt is null)
            _nodes[id] = node with { RetiredAt = now };
        return Task.CompletedTask;
    }

    private (RedirectorCredentialId Id, StoredCredential Credential)? FindCredential(string secret)
    {
        var digest = SecretDigest.DigestOf(secret);
        if (digest is null)
            return null;
        var hex = Convert.ToHexString(digest);
        foreach (var (id, credential) in _credentials)
        {
            if (Convert.ToHexString(credential.Digest).Equals(hex, StringComparison.OrdinalIgnoreCase))
                return (id, credential);
        }

        return null;
    }

    private sealed record StoredCredential(
        byte[] Digest,
        string Name,
        OperatorId IssuedBy,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        RedirectorNodeId? EnrolledNodeId,
        DateTimeOffset? RevokedAt);
}

/// <summary>
/// Digest comparison for stored node secrets. Constant-time over the hex form
/// so a presented secret's digest is never compared early-exit.
/// </summary>
internal static class SecretDigestComparer
{
    public static bool Same(string storedHex, string presentedSecret)
    {
        var digest = SecretDigest.DigestOf(presentedSecret);
        return digest is not null && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(storedHex),
            digest);
    }
}
