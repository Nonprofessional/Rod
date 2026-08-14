using System.Collections.Concurrent;

namespace Rod.CoreState.Engagements;

/// <summary>
/// In-memory <see cref="IEngagementRepository"/> for the walking skeleton
/// ( -- no Postgres yet). State lives in process and is lost on
/// restart; the port keeps callers agnostic to that.
/// </summary>
public sealed class InMemoryEngagementRepository : IEngagementRepository
{
    private readonly ConcurrentDictionary<EngagementId, Engagement> _engagements = new();

    public Task<Engagement?> FindAsync(EngagementId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_engagements.TryGetValue(id, out var engagement) ? engagement : null);

    public async Task<Engagement> GetOrThrowAsync(EngagementId id, CancellationToken cancellationToken = default)
        => await FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Engagement {id} does not exist.");

    public Task<IReadOnlyList<Engagement>> ListAsync(CancellationToken cancellationToken = default)
    {
        var all = _engagements.Values.OrderBy(e => e.CreatedAt).ToArray();
        return Task.FromResult<IReadOnlyList<Engagement>>(all);
    }

    public Task SaveAsync(Engagement engagement, CancellationToken cancellationToken = default)
    {
        _engagements[engagement.Id] = engagement;
        return Task.CompletedTask;
    }
}
