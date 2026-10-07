namespace Rod.CoreState.Snippets;

/// <summary>
/// The durable home for engagement-scoped task snippets. Snippets persist
/// with the engagement -- in-memory by default (the process's lifetime,
/// like the rest of core state), Postgres-backed when the connection
/// string is set. The store is plain CRUD: replay happens in the console,
/// which issues each step through the ordinary tasking path, so nothing
/// here needs bookkeeping beyond the saved rows.
/// </summary>
public interface ITaskSnippetStore
{
    /// <summary>Saves a snippet (the id is the identity; snippets are immutable in practice, saved once).</summary>
    Task SaveAsync(TaskSnippet snippet, CancellationToken cancellationToken = default);

    /// <summary>The snippet, or null when unknown.</summary>
    Task<TaskSnippet?> FindAsync(TaskSnippetId id, CancellationToken cancellationToken = default);

    /// <summary>The snippets belonging to one engagement, oldest first.</summary>
    Task<IReadOnlyList<TaskSnippet>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a snippet. Returns false when it was not stored.</summary>
    Task<bool> RemoveAsync(TaskSnippetId id, CancellationToken cancellationToken = default);
}
