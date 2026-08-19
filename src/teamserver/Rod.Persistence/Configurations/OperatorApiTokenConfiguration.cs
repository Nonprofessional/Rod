using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Operators;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the operator API-token persistence model (architecture.md
/// Sec 9 -- the identity model's API tokens). One row per minted token: the
/// SHA-256 digest of the secret (never the plaintext), the owning operator, and
/// the mint time. Revocation deletes the row; the next request presenting the
/// secret finds nothing.
/// </summary>
/// <remarks>
/// The durable analogue of the in-memory
/// <see cref="global::Rod.CoreState.Operators.InMemoryOperatorApiTokenStore"/>;
/// it lives in the persistence layer so the domain stays free of any
/// stored-secret shape, exactly like the password-verifier row.
/// </remarks>
internal sealed class StoredOperatorApiToken
{
    public OperatorApiTokenId TokenId { get; set; }
    public OperatorId OperatorId { get; set; }
    public byte[] Hash { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class StoredOperatorApiTokenConfiguration
    : IEntityTypeConfiguration<StoredOperatorApiToken>
{
    public void Configure(EntityTypeBuilder<StoredOperatorApiToken> builder)
    {
        builder.ToTable("operator_api_tokens");

        builder.HasKey(t => t.TokenId);
        builder.Property(t => t.TokenId)
            .HasConversion(IdConverters.OperatorApiTokenId)
            .HasColumnName("token_id");

        builder.Property(t => t.OperatorId)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("operator_id");

        // The digest lookup a presented secret resolves through; unique because
        // the mint draws 256 random bits.
        builder.Property(t => t.Hash).HasColumnName("hash").IsRequired();
        builder.HasIndex(t => t.Hash).IsUnique().HasDatabaseName("ix_operator_api_tokens_hash");

        builder.Property(t => t.CreatedAt).HasColumnName("created_at");

        // A token cannot outlive its operator: cascade delete on the shared
        // operator_id keeps the row in sync if an operator is ever removed.
        builder.HasOne<global::Rod.CoreState.Operators.Operator>()
            .WithMany()
            .HasForeignKey(t => t.OperatorId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
