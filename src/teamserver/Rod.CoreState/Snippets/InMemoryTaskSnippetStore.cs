using System.Collections.Concurrent;

namespace Rod.CoreState.Snippets;

/// <summary>
/// The in-memory <see cref="ITaskSnippetStore"/> twin: snippets live as
/// long as the process. The console reads through the same port either
/// way, so the Postgres swap changes durability, never behavior.
/// </summary>
public sealed class InMemoryTaskSnippetStore : ITaskSnippetStore
{
    private readonly ConcurrentDictionary<TaskSnippetId, TaskSnippet> _snippets = new();

    public Task SaveAsync(TaskSnippet snippet, CancellationToken cancellationToken = default)
    {
        _snippets[snippet.Id] = snippet;
        return Task.CompletedTask;
    }

    public Task<TaskSnippet?> FindAsync(TaskSnippetId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_snippets.TryGetValue(id, out var snippet) ? snippet : null);

    public Task<IReadOnlyList<TaskSnippet>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TaskSnippet>>(
            _snippets.Values
                .Where(s => s.EngagementId == engagementId)
                .OrderBy(s => s.CreatedAt)
                .ThenBy(s => s.Id.Value)
                .ToArray());

    public Task<bool> RemoveAsync(TaskSnippetId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_snippets.TryRemove(id, out _));
}
