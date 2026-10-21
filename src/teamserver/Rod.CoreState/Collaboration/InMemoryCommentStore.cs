using System.Collections.Concurrent;

namespace Rod.CoreState.Collaboration;

/// <summary>
/// The in-memory <see cref="ICommentStore"/> twin: comments live as long as
/// the process. The console reads through the same port either way, so the
/// Postgres swap changes durability, never behavior.
/// </summary>
public sealed class InMemoryCommentStore : ICommentStore
{
    private readonly ConcurrentDictionary<CommentId, Comment> _comments = new();

    public Task SaveAsync(Comment comment, CancellationToken cancellationToken = default)
    {
        _comments[comment.Id] = comment;
        return Task.CompletedTask;
    }

    public Task<Comment?> FindAsync(CommentId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_comments.TryGetValue(id, out var comment) ? comment : null);

    public Task<bool> RemoveAsync(CommentId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_comments.TryRemove(id, out _));

    public Task<IReadOnlyList<Comment>> ListBySubjectAsync(
        EngagementId engagementId,
        CommentSubjectKind subjectKind,
        Guid subjectId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Comment>>(
            _comments.Values
                .Where(c => c.EngagementId == engagementId
                    && c.SubjectKind == subjectKind
                    && c.SubjectId == subjectId)
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id.Value)
                .ToArray());

    public Task<NewestWindowPage<Comment>> ListPageAsync(
        EngagementId engagementId,
        int limit,
        DateTimeOffset? before,
        Guid? beforeId,
        CancellationToken cancellationToken = default)
    {
        var ordered = _comments.Values
            .Where(c => c.EngagementId == engagementId)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id.Value)
            .ToArray();
        return Task.FromResult(CollaborationWindow.TakeNewest(
            ordered, limit, before, beforeId, c => c.CreatedAt, c => c.Id.Value));
    }
}
