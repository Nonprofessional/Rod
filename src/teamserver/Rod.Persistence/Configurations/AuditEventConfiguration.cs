using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.Audit;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="AuditEvent"/> (ADR 0003). The positional record
/// constructor binds all 13 fields by name; the store holds the chained event
/// verbatim (<see cref="AuditEvent.PreviousHash"/> and <see cref="AuditEvent.Hash"/>
/// included) so a reloaded trail round-trips through <see cref="AuditChain.VerifyTrail"/>
/// unchanged. The audit layer crosses the boundary with plain <see cref="Guid"/>
/// ids, so no id value converter is needed here.
/// </summary>
/// <remarks>
/// The append sequence (a per-engagement monotonic column the store recovers the
/// chain head from) is added by the durable audit store adapter (Phase 4); this
/// configuration captures the columns the InitialCreate migration emits so the
/// schema is stable up front.
/// </remarks>
internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");

        builder.HasKey(e => e.EventId);
        builder.Property(e => e.EventId).HasColumnName("event_id");
        builder.Property(e => e.EngagementId).HasColumnName("engagement_id");
        builder.Property(e => e.OperatorId).HasColumnName("operator_id");
        builder.Property(e => e.ImplantId).HasColumnName("implant_id");
        builder.Property(e => e.TaskId).HasColumnName("task_id");
        builder.Property(e => e.Verb).HasColumnName("verb").HasMaxLength(256).IsRequired();
        builder.Property(e => e.Kind).HasColumnName("kind");
        builder.Property(e => e.Payload).HasColumnName("payload").IsRequired();
        builder.Property(e => e.Output).HasColumnName("output");
        builder.Property(e => e.Outcome).HasColumnName("outcome").IsRequired();
        builder.Property(e => e.At).HasColumnName("at");
        builder.Property(e => e.PreviousHash).HasColumnName("previous_hash").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Hash).HasColumnName("hash").HasMaxLength(64).IsRequired();

        // Per-engagement chain head recovery (Phase 4 adapter) reads the highest-
        // sequence row per engagement; the sequence column is part of the schema
        // so InitialCreate captures it now.
        builder.Property<long>("AppendSequence").HasColumnName("append_sequence");

        // Scoping + head recovery both key off the engagement, so index it.
        builder.HasIndex(e => e.EngagementId).HasDatabaseName("ix_audit_events_engagement_id");
    }
}
