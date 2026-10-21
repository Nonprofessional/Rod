using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.CoreState.Operators;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for engagement-scoped collaboration comments
/// (architecture.md Sec 15.3, ADR-005). A Persistence-owned mirror of core
/// state's <see cref="Comment"/> aggregate: the row is edited and deleted in
/// place, and it is mutable process knowledge -- never audit-chain content,
/// so nothing here feeds the trail.
/// </summary>
internal sealed class StoredComment
{
    public Guid Id { get; set; }
    public EngagementId EngagementId { get; set; }

    /// <summary>The subject kind, stored as int by convention.</summary>
    public CommentSubjectKind SubjectKind { get; set; }
    public Guid SubjectId { get; set; }
    public OperatorId AuthorId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }

    public static StoredComment From(Comment comment) => new()
    {
        Id = comment.Id.Value,
        EngagementId = comment.EngagementId,
        SubjectKind = comment.SubjectKind,
        SubjectId = comment.SubjectId,
        AuthorId = comment.AuthorId,
        Body = comment.Body,
        CreatedAt = comment.CreatedAt,
        EditedAt = comment.EditedAt,
    };

    public Comment ToDomain() => new(
        new CommentId(Id),
        EngagementId,
        SubjectKind,
        SubjectId,
        AuthorId,
        Body,
        CreatedAt,
        EditedAt);
}

internal sealed class CommentConfiguration : IEntityTypeConfiguration<StoredComment>
{
    public void Configure(EntityTypeBuilder<StoredComment> builder)
    {
        builder.ToTable("comments");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("comment_id");

        builder.Property(c => c.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(c => c.SubjectKind).HasColumnName("subject_kind");
        builder.Property(c => c.SubjectId).HasColumnName("subject_id");
        builder.Property(c => c.AuthorId)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("author_id");
        builder.Property(c => c.Body).HasColumnName("body");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.EditedAt).HasColumnName("edited_at");

        // The panel reads by subject and the timeline pages by engagement;
        // both reads are scoped.
        builder.HasIndex(c => c.EngagementId).HasDatabaseName("ix_comments_engagement_id");
        builder.HasIndex(c => new { c.EngagementId, c.SubjectKind, c.SubjectId })
            .HasDatabaseName("ix_comments_engagement_subject");
    }
}
