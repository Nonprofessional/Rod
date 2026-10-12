using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;

namespace Rod.Persistence.Configurations;

/// <summary>
/// The enrolled redirector node, durable (architecture.md Sec 8): deployment
/// capacity that outlives a restart. The node secret rides as its SHA-256
/// digest alone -- hash-only like every stored credential in the platform.
/// </summary>
internal sealed class StoredRedirectorNode
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid EnrolledBy { get; set; }
    public DateTimeOffset EnrolledAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string? LastAddress { get; set; }
    public string? ReportedVersion { get; set; }
    public string? ReportedCommit { get; set; }
    public string SecretDigest { get; set; } = string.Empty;
}

internal sealed class StoredRedirectorNodeConfiguration : IEntityTypeConfiguration<StoredRedirectorNode>
{
    public void Configure(EntityTypeBuilder<StoredRedirectorNode> builder)
    {
        builder.ToTable("redirector_nodes");

        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).HasColumnName("node_id");
        builder.Property(n => n.Name).HasColumnName("name").HasMaxLength(200);
        builder.Property(n => n.EnrolledBy).HasColumnName("enrolled_by");
        builder.Property(n => n.EnrolledAt).HasColumnName("enrolled_at");
        builder.Property(n => n.RetiredAt).HasColumnName("retired_at");
        builder.Property(n => n.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(n => n.LastAddress).HasColumnName("last_address").HasMaxLength(100);
        builder.Property(n => n.ReportedVersion).HasColumnName("reported_version").HasMaxLength(64);
        builder.Property(n => n.ReportedCommit).HasColumnName("reported_commit").HasMaxLength(64);
        builder.Property(n => n.SecretDigest).HasColumnName("secret_digest").HasMaxLength(128);
    }
}

/// <summary>
/// A pre-provisioned enrollment credential, durable: the digest the fleet
/// resolves a presented secret against, the stamps of its lifecycle, and the
/// node that spent it. Revoked and spent rows stay resolvable so an attempt
/// reads its honest reason.
/// </summary>
internal sealed class StoredRedirectorCredential
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid IssuedBy { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public byte[] SecretDigest { get; set; } = [];
    public Guid? EnrolledNodeId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

internal sealed class StoredRedirectorCredentialConfiguration : IEntityTypeConfiguration<StoredRedirectorCredential>
{
    public void Configure(EntityTypeBuilder<StoredRedirectorCredential> builder)
    {
        builder.ToTable("redirector_credentials");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("credential_id");
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(200);
        builder.Property(c => c.IssuedBy).HasColumnName("issued_by");
        builder.Property(c => c.IssuedAt).HasColumnName("issued_at");
        builder.Property(c => c.ExpiresAt).HasColumnName("expires_at");
        builder.Property(c => c.SecretDigest).HasColumnName("secret_digest");
        builder.Property(c => c.EnrolledNodeId).HasColumnName("enrolled_node_id");
        builder.Property(c => c.RevokedAt).HasColumnName("revoked_at");

        // The ledger reads by issue time; an enrolled credential's node is
        // the roster join.
        builder.HasIndex(c => c.IssuedAt).HasDatabaseName("ix_redirector_credentials_issued_at");
        builder.HasIndex(c => c.EnrolledNodeId).HasDatabaseName("ix_redirector_credentials_enrolled_node_id");
    }
}
