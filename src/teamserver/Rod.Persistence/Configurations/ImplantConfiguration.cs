using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState.Implants;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Implant"/> (ADR 0003). Private parameterized
/// constructor binds the get-only scalars; <see cref="Implant.RetiredAt"/>
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
        // The optional time fuse the artifact reported at enroll: null is the
        // open-ended posture. Nullable CLR type -> nullable column; implants
        // recorded before the change always carried a date, so no backfill.
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
        // The host identity reported at enroll (the device dimension of the
        // fleet): nullable text, null for implants that predate the field.
        builder.Property(i => i.Hostname).HasColumnName("hostname");
        builder.Property(i => i.Os).HasColumnName("os");
        builder.Property(i => i.Arch).HasColumnName("arch");
        builder.Property(i => i.Username).HasColumnName("username");
        // The listener whose socket carried the enrollment, when the transport
        // could attribute one; the listener-delete guard counts against it.
        builder.Property(i => i.EnrolledViaListenerId).HasColumnName("enrolled_via_listener_id");
        // The durable heartbeat: when the teamserver last heard from this
        // implant, kept after the session is gone. Null for implants that
        // predate the stamp.
        builder.Property(i => i.LastSeenAt).HasColumnName("last_seen_at");
        // IsRetired is a computed expression; never mapped.

        // Engagement scoping is structural: index the engagement column so
        // ListByEngagementAsync stays a cheap scoped read.
        builder.HasIndex(i => i.EngagementId).HasDatabaseName("ix_implants_engagement_id");

        builder.UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
