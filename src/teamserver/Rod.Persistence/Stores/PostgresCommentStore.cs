using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.Persistence.Configurations;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="ICommentStore"/>: engagement-scoped
/// comments survive a teamserver restart. Rows are edited and deleted in
/// place under the service's window rule; the store carries no rule of its
/// own.
/// </summary>
internal sealed class PostgresCommentStore : ICommentStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresCommentStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task SaveAsync(Comment comment, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Comments.FindAsync(new object[] { comment.Id.Value }, cancellationToken);
        if (stored is null)
        {
            db.Comments.Add(StoredComment.From(comment));
        }
        else
        {
            // The row mirrors the aggregate whole; the id and the engagement
            // binding never change, everything else may have.
            db.Entry(stored).CurrentValues.SetValues(StoredComment.From(comment));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Comment?> FindAsync(CommentId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Comments.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id.Value, cancellationToken);
        return stored?.ToDomain();
    }

    public async Task<bool> RemoveAsync(CommentId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Comments.FindAsync(new object[] { id.Value }, cancellationToken);
        if (stored is null)
            return false;
        db.Comments.Remove(stored);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<Comment>> ListBySubjectAsync(
        EngagementId engagementId,
        CommentSubjectKind subjectKind,
        Guid subjectId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Comments.AsNoTracking()
            .Where(c => c.EngagementId == engagementId
                && c.SubjectKind == subjectKind
                && c.SubjectId == subjectId)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(c => c.ToDomain()).ToArray();
    }

    public async Task<NewestWindowPage<Comment>> ListPageAsync(
        EngagementId engagementId,
        int limit,
        DateTimeOffset? before,
        Guid? beforeId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        IQueryable<StoredComment> query = db.Comments
            .AsNoTracking()
            .Where(c => c.EngagementId == engagementId);
        if (before is { } afterAt && beforeId is { } afterId)
        {
            query = query.Where(c => c.CreatedAt < afterAt
                || (c.CreatedAt == afterAt && c.Id.CompareTo(afterId) < 0));
        }

        var newestFirst = await query
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = newestFirst.Count > limit;
        var items = newestFirst.Take(limit)
            .Select(c => c.ToDomain())
            .ToArray();
        Array.Reverse(items); // Oldest first within the page.
        return new NewestWindowPage<Comment>(items, hasMore);
    }
}
