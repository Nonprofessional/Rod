using System.Collections.Concurrent;

namespace Rod.CoreState.Listeners;

/// <summary>
/// In-memory <see cref="IListenerStore"/>: definitions live as long as the
/// process, the same posture as the rest of core state without Postgres -- a
/// dev restart rebinds the configured operator front only, and the engagements
/// themselves are gone with it.
/// </summary>
public sealed class InMemoryListenerStore : IListenerStore
{
    private readonly ConcurrentDictionary<Guid, ListenerDefinition> _definitions = new();

    public Task SaveAsync(ListenerDefinition definition, CancellationToken cancellationToken = default)
    {
        _definitions[definition.Id] = definition;
        return Task.CompletedTask;
    }

    public Task<ListenerDefinition?> FindAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_definitions.TryGetValue(id, out var found) ? found : null);

    public Task<IReadOnlyList<ListenerDefinition>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ListenerDefinition>>(
            _definitions.Values.OrderBy(d => d.CreatedAt).ToArray());

    public Task<IReadOnlyList<ListenerDefinition>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ListenerDefinition>>(
            _definitions.Values.Where(d => d.EngagementId == engagementId)
                .OrderBy(d => d.CreatedAt).ToArray());

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_definitions.TryRemove(id, out _));
}
