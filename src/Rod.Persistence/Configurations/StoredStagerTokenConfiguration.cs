using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the stager-token persistence model (ADR 0003). The
/// <see cref="IStagerTokenService"/> stores only the SHA-256 hash of the minted
/// secret, never the plaintext, alongside the engagement, issuer, expiry, and the
/// remaining-use counter it decrements on redeem. This is the durable analogue of
/// the in-memory service's private <c>StoredToken</c> record; it lives here in the
/// persistence layer so the domain stays free of any stored-secret shape.
/// </summary>
/// <remarks>
/// Concurrency on redeem is handled at the adapter (Phase 3) rather than on this
/// model; see ADR 0003. The schema is captured here so the InitialCreate
/// migration is stable.
/// </remarks>
internal sealed class StoredStagerToken
{
    public StagerTokenId Id { get; set; }
    public EngagementId EngagementId { get; set; }
    public OperatorId IssuedBy { get; set; }
    public byte[] Hash { get; set; } = Array.Empty<byte>();
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int MaxUses { get; set; }
    public int RemainingUses { get; set; }
}

internal sealed class StoredStagerTokenConfiguration : IEntityTypeConfiguration<StoredStagerToken>
{
    public void Configure(EntityTypeBuilder<StoredStagerToken> builder)
    {
        builder.ToTable("stager_tokens");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .HasConversion(IdConverters.StagerTokenId)
            .HasColumnName("stager_token_id");

        builder.Property(t => t.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(t => t.IssuedBy)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("issued_by");

        // The hash is the only thing the service keeps of the secret: a 32-byte
        // SHA-256 digest looked up on redeem. Stored as bytea; never the secret.
        builder.Property(t => t.Hash).HasColumnName("secret_hash").IsRequired();
        builder.Property(t => t.IssuedAt).HasColumnName("issued_at");
        builder.Property(t => t.ExpiresAt).HasColumnName("expires_at");
        builder.Property(t => t.MaxUses).HasColumnName("max_uses");
        builder.Property(t => t.RemainingUses).HasColumnName("remaining_uses");
    }
}
