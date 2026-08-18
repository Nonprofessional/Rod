using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState.Implants;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Implant"/> (ADR 0003). Private parameterized
/// constructor (8 params) binds the get-only scalars; <see cref="Implant.RetiredAt"/>
/// has a private setter written via its backing field. The typed ids and the
/// nullable parent id all map to Postgres <c>uuid</c>.
/// </summary>
internal sealed class ImplantConfiguration : IEntityTypeConfiguration<Implant>
{
    public void Configure(EntityTypeBuilder<Implant> builder)
    {
        builder.ToTable("implants");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id)
            .HasConversion(IdConverters.ImplantId)
            .HasColumnName("implant_id");

        builder.Property(i => i.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(i => i.KillDate).HasColumnName("kill_date");
        // ImplantClass is an int column, matching the audit hash's (int)Kind form.
        builder.Property(i => i.Class).HasColumnName("class");
        builder.Property(i => i.CreatedAt).HasColumnName("created_at");
        builder.Property(i => i.DeployedBy)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("deployed_by");
        builder.Property(i => i.ParentImplantId)
            .HasConversion(IdConverters.ImplantId)
            .HasColumnName("parent_implant_id");
        builder.Property(i => i.RetiredAt).HasColumnName("retired_at");
        // The sticky replay-nonce negotiation flag (architecture.md Sec 9);
        // false for implants that predate the arm, which never advertised it.
        builder.Property(i => i.ReplayNonces).HasColumnName("replay_nonces");
        // IsRetired is a computed expression; never mapped.

        // Engagement scoping is structural: index the engagement column so
        // ListByEngagementAsync stays a cheap scoped read.
        builder.HasIndex(i => i.EngagementId).HasDatabaseName("ix_implants_engagement_id");

        builder.UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
