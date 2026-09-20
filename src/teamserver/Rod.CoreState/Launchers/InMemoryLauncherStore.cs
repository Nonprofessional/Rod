using System.Collections.Concurrent;
using Rod.CoreState.Engagements;

namespace Rod.CoreState.Launchers;

/// <summary>
/// In-memory <see cref="ILauncherStore"/>: rows live as long as the process,
/// the same posture as the rest of core state without Postgres. The
/// credentials themselves still live in the token store with their own
/// expiry, so an in-memory restart loses the list but no control -- the
/// credentials die on their own clocks.
/// </summary>
public sealed class InMemoryLauncherStore : ILauncherStore
{
    private readonly ConcurrentDictionary<Guid, Launcher> _rows = new();

    public Task SaveAsync(Launcher launcher, CancellationToken cancellationToken = default)
    {
        _rows[launcher.Id.Value] = launcher;
        return Task.CompletedTask;
    }

    public Task<Launcher?> FindAsync(LauncherId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_rows.TryGetValue(id.Value, out var found) ? found : null);

    public Task<IReadOnlyList<Launcher>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Launcher>>(
            _rows.Values.Where(l => l.EngagementId == engagement)
                .OrderByDescending(l => l.CreatedAt).ToArray());

    public Task<bool> RemoveAsync(LauncherId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_rows.TryRemove(id.Value, out _));
}
