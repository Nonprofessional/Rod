using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the core-state <see cref="Task"/> entity (ADR 0003).
/// Private parameterized constructor binds the get-only scalars; the lifecycle
/// fields (<see cref="Task.Status"/>, <see cref="Task.Output"/>,
/// <see cref="Task.Outcome"/>, <see cref="Task.DispatchedAt"/>,
/// <see cref="Task.CompletedAt"/>) carry private setters EF writes through their
/// backing fields. The entity name shadows <see cref="System.Threading.Tasks.Task"/>,
/// so the alias is pinned here.
/// </summary>
internal sealed class TaskConfiguration : IEntityTypeConfiguration<Task>
{
    public void Configure(EntityTypeBuilder<Task> builder)
    {
        builder.ToTable("tasks");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .HasConversion(IdConverters.TaskId)
            .HasColumnName("task_id");

        builder.Property(t => t.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(t => t.ImplantId)
            .HasConversion(IdConverters.ImplantId)
            .HasColumnName("implant_id");
        builder.Property(t => t.IssuedBy)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("issued_by");
        builder.Property(t => t.Verb).HasColumnName("verb").HasMaxLength(256).IsRequired();
        builder.Property(t => t.Arguments).HasColumnName("arguments").IsRequired();
        builder.Property(t => t.Status).HasColumnName("status");
        builder.Property(t => t.Output).HasColumnName("output");
        builder.Property(t => t.Outcome).HasColumnName("outcome");
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.DispatchedAt).HasColumnName("dispatched_at");
        builder.Property(t => t.CompletedAt).HasColumnName("completed_at");

        // Scoped reads (ListByEngagementAsync, ListByImplantAsync) and the FIFO
        // dispatch lookup (NextPendingAsync) filter by engagement and implant, so
        // index both; the enqueue sequence that orders FIFO is a property of the
        // adapter's persistence model, not this domain entity.
        builder.HasIndex(t => t.EngagementId).HasDatabaseName("ix_tasks_engagement_id");
        builder.HasIndex(t => t.ImplantId).HasDatabaseName("ix_tasks_implant_id");

        builder.UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
