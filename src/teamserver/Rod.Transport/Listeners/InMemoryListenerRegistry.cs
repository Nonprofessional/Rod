using System.Collections.Concurrent;

namespace Rod.Transport.Listeners;

/// <summary>
/// In-memory <see cref="IListenerRegistry"/> for the walking skeleton. Listeners
/// live in a process-local map keyed by id. State is lost on restart, which is
/// correct for disposable infrastructure (architecture.md Sec 8). The port keeps
/// callers agnostic to that.
/// </summary>
public sealed class InMemoryListenerRegistry : IListenerRegistry
{
    private readonly ConcurrentDictionary<ListenerId, Listener> _listeners = new();
    private readonly TimeProvider _clock;

    public InMemoryListenerRegistry()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Constructs the registry with a specific clock. Tests inject a fake so
    /// repoint timestamps are deterministic; the host uses the system clock.
    /// </summary>
    public InMemoryListenerRegistry(TimeProvider clock)
    {
        _clock = clock;
    }

    public Task RegisterAsync(Listener listener, CancellationToken cancellationToken = default)
    {
        listener.Start();
        _listeners[listener.Id] = listener;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Listener>> ListAsync(CancellationToken cancellationToken = default)
    {
        var ordered = _listeners.Values.OrderBy(l => l.CreatedAt).ToArray();
        return Task.FromResult<IReadOnlyList<Listener>>(ordered);
    }

    public Task<Listener?> FindAsync(ListenerId listener, CancellationToken cancellationToken = default)
        => Task.FromResult(_listeners.TryGetValue(listener, out var found) ? found : null);

    public Task<Listener?> RepointAsync(
        ListenerId listener,
        string publicEndpoint,
        CancellationToken cancellationToken = default)
    {
        if (_listeners.TryGetValue(listener, out var found))
        {
            found.Repoint(publicEndpoint, _clock.GetUtcNow());
            return Task.FromResult<Listener?>(found);
        }

        return Task.FromResult<Listener?>(null);
    }
}
