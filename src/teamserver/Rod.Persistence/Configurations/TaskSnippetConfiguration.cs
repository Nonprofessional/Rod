using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Operators;
using Rod.CoreState.Snippets;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the engagement-scoped task snippets. One row per
/// snippet with its steps serialized as JSON -- a snippet is immutable in
/// practice (saved once, deleted outright), so the steps need no
/// change-tracking shape. A Persistence-owned mirror of core state's
/// <see cref="TaskSnippet"/> aggregate; the domain stays free of any
/// stored shape.
/// </summary>
internal sealed class StoredTaskSnippet
{
    private static readonly JsonSerializerOptions StepJson = new(JsonSerializerDefaults.General);

    public Guid Id { get; set; }
    public EngagementId EngagementId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The ordered steps as a JSON array of {verb, arguments} objects.</summary>
    public string StepsJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public OperatorId CreatedBy { get; set; }

    public static StoredTaskSnippet From(TaskSnippet snippet) => new()
    {
        Id = snippet.Id.Value,
        EngagementId = snippet.EngagementId,
        Name = snippet.Name,
        StepsJson = JsonSerializer.Serialize(snippet.Steps, StepJson),
        CreatedAt = snippet.CreatedAt,
        CreatedBy = snippet.CreatedBy,
    };

    public TaskSnippet ToDomain() => new(
        new TaskSnippetId(Id),
        EngagementId,
        Name,
        JsonSerializer.Deserialize<IReadOnlyList<TaskSnippetStep>>(StepsJson, StepJson) ?? Array.Empty<TaskSnippetStep>(),
        CreatedAt,
        CreatedBy);
}

internal sealed class TaskSnippetConfiguration : IEntityTypeConfiguration<StoredTaskSnippet>
{
    public void Configure(EntityTypeBuilder<StoredTaskSnippet> builder)
    {
        builder.ToTable("task_snippets");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("task_snippet_id");

        builder.Property(s => s.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(s => s.Name).HasColumnName("name").HasMaxLength(200);
        builder.Property(s => s.StepsJson).HasColumnName("steps_json");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.CreatedBy)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("created_by");

        // The operator API lists by engagement; that read is scoped.
        builder.HasIndex(s => s.EngagementId).HasDatabaseName("ix_task_snippets_engagement_id");
    }
}
