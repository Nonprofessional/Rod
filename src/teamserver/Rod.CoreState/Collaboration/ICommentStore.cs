namespace Rod.CoreState.Collaboration;

/// <summary>
/// The durable home for engagement-scoped collaboration comments
/// (architecture.md Sec 15.3, ADR-005). Comments are mutable process
/// knowledge: the row is edited and deleted in place, it never enters the
/// audit chain, and the timeline reads it as an ordinary, explicitly
/// mutable event. In-memory by default (the process's lifetime), Postgres-
/// backed when the connection string is set.
/// </summary>
public interface ICommentStore
{
    /// <summary>Saves a comment (the id is the identity; an edit re-saves its row).</summary>
    Task SaveAsync(Comment comment, CancellationToken cancellationToken = default);

    /// <summary>The comment, or null when unknown.</summary>
    Task<Comment?> FindAsync(CommentId id, CancellationToken cancellationToken = default);

    /// <summary>Deletes a comment. Returns false when it was not stored.</summary>
    Task<bool> RemoveAsync(CommentId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The comments attached to one subject, oldest first -- the panel read.
    /// </summary>
    Task<IReadOnlyList<Comment>> ListBySubjectAsync(
        EngagementId engagementId,
        CommentSubjectKind subjectKind,
        Guid subjectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One newest-window page of the engagement's comments, oldest first
    /// within the page -- the timeline read, keyed on (CreatedAt, Id) with
    /// the same keyset the audit listing pages on.
    /// </summary>
    Task<NewestWindowPage<Comment>> ListPageAsync(
        EngagementId engagementId,
        int limit,
        DateTimeOffset? before,
        Guid? beforeId,
        CancellationToken cancellationToken = default);
}
