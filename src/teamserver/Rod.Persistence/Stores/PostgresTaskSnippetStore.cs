using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Snippets;
using Rod.Persistence.Configurations;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="ITaskSnippetStore"/>: engagement-scoped
/// task snippets survive a teamserver restart. Plain rows, no concurrency
/// hazard -- snippets are saved once and deleted outright, never mutated
/// in place, so there is no lost-update window to close.
/// </summary>
internal sealed class PostgresTaskSnippetStore : ITaskSnippetStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresTaskSnippetStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task SaveAsync(TaskSnippet snippet, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.TaskSnippets.FindAsync(new object[] { snippet.Id.Value }, cancellationToken);
        if (stored is null)
        {
            db.TaskSnippets.Add(StoredTaskSnippet.From(snippet));
        }
        else
        {
            // The row mirrors the aggregate whole; the id and the engagement
            // binding never change, everything else may have.
            db.Entry(stored).CurrentValues.SetValues(StoredTaskSnippet.From(snippet));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<TaskSnippet?> FindAsync(TaskSnippetId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.TaskSnippets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id.Value, cancellationToken);
        return stored?.ToDomain();
    }

    public async Task<IReadOnlyList<TaskSnippet>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var mine = await db.TaskSnippets.AsNoTracking()
            .Where(s => s.EngagementId == engagementId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
        return mine.Select(s => s.ToDomain()).ToArray();
    }

    public async Task<bool> RemoveAsync(TaskSnippetId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.TaskSnippets.FindAsync(new object[] { id.Value }, cancellationToken);
        if (stored is null)
            return false;
        db.TaskSnippets.Remove(stored);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
