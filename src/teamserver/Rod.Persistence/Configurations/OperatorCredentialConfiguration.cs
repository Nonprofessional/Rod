using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the operator password-verifier persistence model (ADR
/// 0003). The <see cref="global::Rod.CoreState.Operators.IOperatorCredentialStore"/>
/// stores only the opaque hash produced by the auth layer's password hasher --
/// never a plaintext password -- keyed by operator id. This is the durable
/// analogue of the in-memory
/// <see cref="global::Rod.CoreState.Operators.InMemoryOperatorCredentialStore"/>;
/// it lives in the persistence layer so the domain
/// <see cref="global::Rod.CoreState.Operators.Operator"/> stays free of any
/// stored-secret shape.
/// </summary>
/// <remarks>
/// One credential row per operator: <c>operator_id</c> is both the primary key
/// and the foreign key to <c>operators</c>, so the relationship is one-to-(zero
/// or one). The hash is the only thing kept of the password; an operator that
/// exists but has not been provisioned with a password simply has no row here,
/// and <see cref="global::Rod.CoreState.Operators.IOperatorCredentialStore.FindHashAsync"/>
/// returns null. See the operator-auth ADR for the hash-only rule and its
/// stager-token twin.
/// </remarks>
internal sealed class StoredOperatorCredential
{
    public OperatorId OperatorId { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class StoredOperatorCredentialConfiguration
    : IEntityTypeConfiguration<StoredOperatorCredential>
{
    public void Configure(EntityTypeBuilder<StoredOperatorCredential> builder)
    {
        builder.ToTable("operator_credentials");

        // operator_id is both the primary key and the foreign key to operators,
        // so each operator has at most one credential (one-to-zero-or-one). The
        // typed id maps to a Postgres uuid through IdConverters.OperatorId,
        // matching the operators.operator_id column the foreign key references.
        builder.HasKey(c => c.OperatorId);
        builder.Property(c => c.OperatorId)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("operator_id");

        // The hash is the only thing kept of the password: the opaque verifier
        // the auth layer's password hasher produced (PBKDF2, ASCII). Stored as
        // text; never the plaintext.
        builder.Property(c => c.PasswordHash).HasColumnName("password_hash").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");

        // The credential cannot outlive its operator: a one-to-one foreign key
        // on the shared operator_id with cascade delete keeps the row in sync if
        // an operator is ever removed.
        builder.HasOne<global::Rod.CoreState.Operators.Operator>()
            .WithOne()
            .HasForeignKey<StoredOperatorCredential>(c => c.OperatorId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
