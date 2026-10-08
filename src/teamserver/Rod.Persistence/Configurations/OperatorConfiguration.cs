using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="global::Rod.CoreState.Operators.Operator"/>
/// (ADR 0003). The entity has a public parameterized constructor and get-only
/// properties -- no parameterless ctor, no setters -- so EF Core materializes it
/// via constructor binding: the column-backed properties map to the constructor
/// parameters by name. The typed id maps to a Postgres <c>uuid</c> through
/// <see cref="IdConverters.OperatorId"/>.
/// </summary>
internal sealed class OperatorConfiguration : IEntityTypeConfiguration<global::Rod.CoreState.Operators.Operator>
{
    public void Configure(EntityTypeBuilder<global::Rod.CoreState.Operators.Operator> builder)
    {
        builder.ToTable("operators");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("operator_id");

        builder.Property(o => o.Handle).HasColumnName("handle").HasMaxLength(256).IsRequired();
        builder.Property(o => o.DisplayName).HasColumnName("display_name").HasMaxLength(512).IsRequired();
        // The role scopes (architecture.md Sec 4.5) as their flag set: three
        // bits in an int. The default is the full peer set, so rows that
        // predate scopes keep the reach they were provisioned with.
        builder.Property(o => o.Scopes)
            .HasConversion<int>()
            .HasColumnName("scopes")
            .HasDefaultValue(global::Rod.CoreState.Operators.OperatorScope.All);
        builder.Property(o => o.CreatedAt).HasColumnName("created_at");
    }
}
