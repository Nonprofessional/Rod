using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Redirectors;
using Rod.Persistence.Configurations;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IRedirectorFleet"/> (architecture.md Sec 8):
/// the enrolled fleet survives a teamserver restart, so a reconnecting node
/// authenticates against the record its enrollment wrote. The single-use
/// consume is a conditional update -- two concurrent presentations of one
/// credential cannot both enroll -- and everything else mirrors the in-memory
/// twin's guards.
/// </summary>
internal sealed class PostgresRedirectorFleet : IRedirectorFleet
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresRedirectorFleet(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task<RedirectorCredential> MintCredentialAsync(
        string name,
        OperatorId issuedBy,
        DateTimeOffset issuedAt,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        var expiresAt = issuedAt + (lifetime ?? TimeSpan.FromDays(1));
        var (secret, digest) = SecretDigest.Mint();
        var id = RedirectorCredentialId.New();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        db.RedirectorCredentials.Add(new StoredRedirectorCredential
        {
            Id = id.Value,
            Name = name,
            IssuedBy = issuedBy.Value,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
            SecretDigest = digest,
        });
        await db.SaveChangesAsync(cancellationToken);

        return new RedirectorCredential
        {
            Id = id,
            Secret = secret,
            Name = name,
            IssuedBy = issuedBy,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
        };
    }

    public async Task<IReadOnlyList<RedirectorCredentialState>> ListCredentialsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.RedirectorCredentials.AsNoTracking()
            .OrderByDescending(c => c.IssuedAt)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new RedirectorCredentialState
        {
            Id = new RedirectorCredentialId(r.Id),
            Name = r.Name,
            IssuedBy = new OperatorId(r.IssuedBy),
            IssuedAt = r.IssuedAt,
            ExpiresAt = r.ExpiresAt,
            EnrolledNodeId = r.EnrolledNodeId is { } node ? new RedirectorNodeId(node) : null,
            RevokedAt = r.RevokedAt,
        }).ToArray();
    }

    public async Task RevokeCredentialAsync(
        RedirectorCredentialId id,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Idempotent: a second stamp changes nothing, and an unknown id
        // leaves nothing to enforce.
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await db.RedirectorCredentials
            .Where(c => c.Id == id.Value && c.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, now), cancellationToken);
    }

    public async Task<EnrolledRedirector> EnrollAsync(
        string credentialSecret,
        string? remoteAddress,
        string? version,
        string? commit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var digest = SecretDigest.DigestOf(credentialSecret)
            ?? throw new RedirectorEnrollException(
                RedirectorEnrollReason.Unknown, "The presented credential matches no minted credential.");

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var credential = await db.RedirectorCredentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.SecretDigest == digest, cancellationToken);
        if (credential is null)
            throw new RedirectorEnrollException(
                RedirectorEnrollReason.Unknown, "The presented credential matches no minted credential.");
        if (credential.RevokedAt is not null)
            throw new RedirectorEnrollException(
                RedirectorEnrollReason.Revoked, $"Credential {credential.Id} has been revoked.");
        if (credential.ExpiresAt <= now)
            throw new RedirectorEnrollException(
                RedirectorEnrollReason.Expired, $"Credential {credential.Id} has passed its expiry.");

        // The single-use consume: the conditional update is the atomic
        // check-then-spend, so two concurrent presentations of one
        // credential cannot both pass. The node id is minted client-side so
        // the row and the node share it without a re-read.
        var nodeId = RedirectorNodeId.New();
        var spent = await db.RedirectorCredentials
            .Where(c => c.Id == credential.Id && c.EnrolledNodeId == null && c.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.EnrolledNodeId, nodeId.Value), cancellationToken);
        if (spent == 0)
            throw new RedirectorEnrollException(
                RedirectorEnrollReason.Spent, $"Credential {credential.Id} has already enrolled a node.");

        var (nodeSecret, nodeDigest) = SecretDigest.Mint();
        var node = new RedirectorNode
        {
            Id = nodeId,
            Name = credential.Name,
            EnrolledBy = new OperatorId(credential.IssuedBy),
            EnrolledAt = now,
            LastSeenAt = now,
            LastAddress = remoteAddress,
            ReportedVersion = version,
            ReportedCommit = commit,
            SecretDigest = Convert.ToHexString(nodeDigest),
        };
        db.RedirectorNodes.Add(new StoredRedirectorNode
        {
            Id = node.Id.Value,
            Name = node.Name,
            EnrolledBy = node.EnrolledBy.Value,
            EnrolledAt = node.EnrolledAt,
            RetiredAt = node.RetiredAt,
            LastSeenAt = node.LastSeenAt,
            LastAddress = node.LastAddress,
            ReportedVersion = node.ReportedVersion,
            ReportedCommit = node.ReportedCommit,
            SecretDigest = node.SecretDigest,
        });
        await db.SaveChangesAsync(cancellationToken);

        return new EnrolledRedirector { Node = node, NodeSecret = nodeSecret };
    }

    public async Task<RedirectorNode> AuthenticateAsync(
        RedirectorNodeId id,
        string nodeSecret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.RedirectorNodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == id.Value, cancellationToken);
        if (stored is null || !SecretDigestComparer.Same(stored.SecretDigest, nodeSecret))
            throw new RedirectorNodeRefusedException($"Node {id} is unknown or its secret is wrong.");
        if (stored.RetiredAt is not null)
            throw new RedirectorNodeRefusedException($"Node {id} has been retired.");

        stored.LastSeenAt = now;
        await db.RedirectorNodes
            .Where(n => n.Id == id.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastSeenAt, now), cancellationToken);
        return ToNode(stored);
    }

    public async Task TouchAsync(
        RedirectorNodeId id,
        string? remoteAddress,
        string? version,
        string? commit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // A touch racing a retire is not an error worth a round trip: the
        // conditional update simply misses a retired or removed row.
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await db.RedirectorNodes
            .Where(n => n.Id == id.Value)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.LastSeenAt, now)
                .SetProperty(n => n.LastAddress, n => remoteAddress ?? n.LastAddress)
                .SetProperty(n => n.ReportedVersion, n => version ?? n.ReportedVersion)
                .SetProperty(n => n.ReportedCommit, n => commit ?? n.ReportedCommit),
                cancellationToken);
    }

    public async Task<IReadOnlyList<RedirectorNode>> ListNodesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.RedirectorNodes.AsNoTracking()
            .OrderByDescending(n => n.EnrolledAt)
            .ToListAsync(cancellationToken);
        return rows.Select(ToNode).ToArray();
    }

    public async Task<RedirectorNode?> FindAsync(RedirectorNodeId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.RedirectorNodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == id.Value, cancellationToken);
        return stored is null ? null : ToNode(stored);
    }

    public async Task RetireAsync(RedirectorNodeId id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await db.RedirectorNodes
            .Where(n => n.Id == id.Value && n.RetiredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.RetiredAt, now), cancellationToken);
    }

    private static RedirectorNode ToNode(StoredRedirectorNode n)
        => new()
        {
            Id = new RedirectorNodeId(n.Id),
            Name = n.Name,
            EnrolledBy = new OperatorId(n.EnrolledBy),
            EnrolledAt = n.EnrolledAt,
            RetiredAt = n.RetiredAt,
            LastSeenAt = n.LastSeenAt,
            LastAddress = n.LastAddress,
            ReportedVersion = n.ReportedVersion,
            ReportedCommit = n.ReportedCommit,
            SecretDigest = n.SecretDigest,
        };
}
