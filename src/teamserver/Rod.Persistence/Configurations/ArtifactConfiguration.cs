using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.Audit;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Artifact"/> (ADR 0003). The positional record
/// constructor binds all 9 fields by name. Content is a Postgres <c>bytea</c>;
/// the operator id is nullable (an artifact may be implant-attached). Scoping is
/// by engagement and by the task the evidence is linked to.
/// </summary>
internal sealed class ArtifactConfiguration : IEntityTypeConfiguration<Artifact>
{
    public void Configure(EntityTypeBuilder<Artifact> builder)
    {
        builder.ToTable("artifacts");

        builder.HasKey(a => a.ArtifactId);
        builder.Property(a => a.ArtifactId).HasColumnName("artifact_id");
        builder.Property(a => a.EngagementId).HasColumnName("engagement_id");
        builder.Property(a => a.TaskId).HasColumnName("task_id");
        builder.Property(a => a.OperatorId).HasColumnName("operator_id");
        builder.Property(a => a.Name).HasColumnName("name").HasMaxLength(512).IsRequired();
        builder.Property(a => a.ContentType).HasColumnName("content_type").HasMaxLength(256).IsRequired();
        builder.Property(a => a.Content).HasColumnName("content").IsRequired();
        builder.Property(a => a.Size).HasColumnName("size");
        builder.Property(a => a.StoredAt).HasColumnName("stored_at");

        // Evidence reads are scoped by engagement and by task.
        builder.HasIndex(a => a.EngagementId).HasDatabaseName("ix_artifacts_engagement_id");
        builder.HasIndex(a => a.TaskId).HasDatabaseName("ix_artifacts_task_id");
    }
}
