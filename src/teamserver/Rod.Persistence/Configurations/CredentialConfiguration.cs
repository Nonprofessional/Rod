using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Collaboration;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for harvested-credential rows (architecture.md
/// Sec 15.3, ADR-005): the persisted landing of a credential-kind task's
/// output, addressed by task-reference placeholders and read by the search
/// index. The secret column carries the implant-reported material -- the
/// same plaintext posture the task transcript holds; the full-text index
/// built over this table covers the metadata columns only, never the
/// secret.
/// </summary>
internal sealed class StoredCredential
{
    public Guid Id { get; set; }
    public EngagementId EngagementId { get; set; }
    public TaskId SourceTaskId { get; set; }
    public ImplantId SourceImplantId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Principal { get; set; } = string.Empty;
    public string? Secret { get; set; }
    public string? Source { get; set; }
    public string? Host { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CapturedAt { get; set; }

    public static StoredCredential From(CredentialRecord record) => new()
    {
        Id = record.Id.Value,
        EngagementId = record.EngagementId,
        SourceTaskId = record.SourceTaskId,
        SourceImplantId = record.SourceImplantId,
        Kind = record.Kind,
        Principal = record.Principal,
        Secret = record.Secret,
        Source = record.Source,
        Host = record.Host,
        Note = record.Note,
        CapturedAt = record.CapturedAt,
    };

    public CredentialRecord ToDomain() => new(
        new CredentialId(Id),
        EngagementId,
        SourceTaskId,
        SourceImplantId,
        Kind,
        Principal,
        Secret,
        Source,
        Host,
        Note,
        CapturedAt);
}

internal sealed class CredentialConfiguration : IEntityTypeConfiguration<StoredCredential>
{
    public void Configure(EntityTypeBuilder<StoredCredential> builder)
    {
        builder.ToTable("credentials");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("credential_id");

        builder.Property(c => c.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(c => c.SourceTaskId)
            .HasConversion(IdConverters.TaskId)
            .HasColumnName("source_task_id");
        builder.Property(c => c.SourceImplantId)
            .HasConversion(IdConverters.ImplantId)
            .HasColumnName("source_implant_id");
        builder.Property(c => c.Kind).HasColumnName("kind");
        builder.Property(c => c.Principal).HasColumnName("principal");
        builder.Property(c => c.Secret).HasColumnName("secret");
        builder.Property(c => c.Source).HasColumnName("source");
        builder.Property(c => c.Host).HasColumnName("host");
        builder.Property(c => c.Note).HasColumnName("note");
        builder.Property(c => c.CapturedAt).HasColumnName("captured_at");

        builder.HasIndex(c => c.EngagementId).HasDatabaseName("ix_credentials_engagement_id");
        builder.HasIndex(c => new { c.EngagementId, c.SourceTaskId })
            .HasDatabaseName("ix_credentials_engagement_source_task");
    }
}
