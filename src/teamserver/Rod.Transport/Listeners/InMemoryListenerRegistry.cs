using System.Collections.Concurrent;
using Rod.Transport.Listeners.Providers;

namespace Rod.Transport.Listeners;

/// <summary>
/// In-memory <see cref="IListenerRegistry"/> by default. Listeners
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

    public Task<bool> RemoveAsync(ListenerId listener, CancellationToken cancellationToken = default)
        => Task.FromResult(_listeners.TryRemove(listener, out _));

    public Task<Listener?> FindByLocalPortAsync(int port, CancellationToken cancellationToken = default)
    {
        // The Kestrel-riding family carries host:port binds and serves the
        // HTTP routes -- enrollment included -- on whatever socket it opens,
        // so the provider registry's shape decides the match: http, https,
        // mtls, and doh today, and any later Kestrel-riding registration
        // without an edit here. The socket-owning family (dns, smb, tcp,
        // quic) never serves HTTP enrollment, so its bind shapes are skipped.
        Listener? found = null;
        foreach (var listener in _listeners.Values)
        {
            if (TransportProviders.Find(listener.Transport) is not KestrelEndpointProvider)
                continue;
            if (!TryParsePort(listener.BindAddress, out var bindPort) || bindPort != port)
                continue;
            if (found is null || listener.CreatedAt < found.CreatedAt)
                found = listener;
        }
        return Task.FromResult(found);
    }

    // The bind address is host:port on every HTTP-shaped transport.
    private static bool TryParsePort(string bindAddress, out int port)
    {
        port = 0;
        var colon = bindAddress.LastIndexOf(':');
        return colon >= 0 && int.TryParse(bindAddress[(colon + 1)..], out port);
    }
}
