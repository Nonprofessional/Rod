using System.Collections.Concurrent;

namespace Rod.Transport.Listeners;

/// <summary>
/// In-memory <see cref="IListenerRegistry"/> for the walking skeleton. Listeners
/// live in a process-local map keyed by id; the public-endpoint lookup filters
/// that map. State is lost on restart, which is correct for disposable
/// infrastructure (architecture.md Sec 8). The port keeps callers agnostic to that.
/// </summary>
public sealed class InMemoryListenerRegistry : IListenerRegistry
{
    private readonly ConcurrentDictionary<ListenerId, Listener> _listeners = new();

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

    public Task<Listener?> GetByPublicEndpointAsync(
        string publicEndpoint,
        CancellationToken cancellationToken = default)
    {
        var match = _listeners.Values.FirstOrDefault(l =>
            string.Equals(l.PublicEndpoint, publicEndpoint, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(match);
    }
}
