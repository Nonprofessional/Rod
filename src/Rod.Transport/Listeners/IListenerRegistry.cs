namespace Rod.Transport.Listeners;

/// <summary>
/// The teamserver's listener registry (architecture.md Sec 8). Holds the bound C2
/// ingress the teamserver is terminating and exposes the read view operators see
/// (<c>GET /listeners</c>). Population happens at startup: <c>UseRodListeners</c>
/// binds each configured listener's socket and registers it here, so the registry
/// reflects what is actually listening rather than what was merely configured.
///
/// Listeners are global infrastructure, so this registry is not engagement-scoped.
/// </summary>
public interface IListenerRegistry
{
    /// <summary>
    /// Registers a listener and marks it running. Called by the host after Kestrel
    /// has bound the listener's socket; not exposed on the operator API (M2.2 is
    /// startup-config + read-only list).
    /// </summary>
    Task RegisterAsync(Listener listener, CancellationToken cancellationToken = default);

    /// <summary>All registered listeners, ordered by creation time.</summary>
    Task<IReadOnlyList<Listener>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The listener, or null when unknown.</summary>
    Task<Listener?> FindAsync(ListenerId listener, CancellationToken cancellationToken = default);

    /// <summary>
    /// The listener serving the given public endpoint, or null. This is the lookup
    /// an implant dialing a redirector resolves to: the public endpoint is the
    /// address baked into its profile, and this maps it back to the listener (and
    /// its bind address) that terminates the connection.
    /// </summary>
    Task<Listener?> GetByPublicEndpointAsync(string publicEndpoint, CancellationToken cancellationToken = default);
}
