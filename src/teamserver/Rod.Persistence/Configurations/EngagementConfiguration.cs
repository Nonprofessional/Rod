using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Engagements;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Engagement"/> (ADR 0003). The aggregate has a
/// private parameterized constructor and get-only scalar properties; EF Core
/// binds them via the private constructor (parameter names match the properties).
/// </summary>
internal sealed class EngagementConfiguration : IEntityTypeConfiguration<Engagement>
{
    public void Configure(EntityTypeBuilder<Engagement> builder)
    {
        builder.ToTable("engagements");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");

        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(512).IsRequired();
        builder.Property(e => e.OwnerId)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("owner_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        // Field access so the private setters on the scalars are read/written
        // through their backing fields, not via property setters the domain
        // deliberately hides.
        builder.UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
