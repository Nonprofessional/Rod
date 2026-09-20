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
        // The contact cadence the implant last advertised (enroll bake, then
        // every changed handshake advertisement): nullable double seconds,
        // null for implants that never reported a cadence.
        builder.Property(i => i.SleepSeconds).HasColumnName("sleep_seconds");
        builder.Property(i => i.JitterSeconds).HasColumnName("jitter_seconds");
        // The listener whose socket carried the enrollment, when the transport
        // could attribute one; the listener-delete guard counts against it.
        builder.Property(i => i.EnrolledViaListenerId).HasColumnName("enrolled_via_listener_id");
        // The durable heartbeat: when the teamserver last heard from this
        // implant, kept after the session is gone. Null for implants that
        // predate the stamp.
        builder.Property(i => i.LastSeenAt).HasColumnName("last_seen_at");
        // The baked carrier set derived at enroll from the build's transport
        // profile: JSON text, the names in bake order. Null for implants that
        // predate the stamp and for builds whose endpoint shapes the
        // derivation does not recognize -- the permissive shape either way.
        builder.Property(i => i.Carriers)
            .HasColumnName("carriers")
            .HasConversion(
                carriers => System.Text.Json.JsonSerializer.Serialize(
                    carriers == null ? Array.Empty<string>() : carriers.ToArray()),
                value => System.Text.Json.JsonSerializer.Deserialize<string[]>(value)
                    ?? Array.Empty<string>())
            .Metadata.SetValueComparer(new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<IReadOnlyList<string>>(
                (a, b) => (a == null && b == null)
                    || (a != null && b != null && a.SequenceEqual(b)),
                c => c == null ? 0 : c.Aggregate(0, (h, s) => h * 31 + s.GetHashCode(StringComparison.Ordinal)),
                c => c == null ? Array.Empty<string>() : c.ToArray()));
        // IsRetired is a computed expression; never mapped.

        // Engagement scoping is structural: index the engagement column so
        // ListByEngagementAsync stays a cheap scoped read.
        builder.HasIndex(i => i.EngagementId).HasDatabaseName("ix_implants_engagement_id");

        builder.UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
