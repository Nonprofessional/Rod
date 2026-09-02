namespace Rod.Transport.Listeners;

/// <summary>
/// The teamserver's listener registry (architecture.md Sec 8). Holds the bound C2
/// ingress the teamserver is terminating and exposes the read view operators see
/// (<c>GET /listeners</c>). Population happens at startup: <c>UseRodListeners</c>
/// binds each configured listener's socket and registers it here, so the registry
/// reflects what is actually listening rather than what was merely configured. At
/// runtime an operator can repoint a listener's public endpoint
/// (<c>POST /listeners/{id}:repoint</c>) to swap a burned redirector without
/// touching the backend; the bind address never changes.
///
/// Listeners are global infrastructure, so this registry is not engagement-scoped.
/// </summary>
public interface IListenerRegistry
{
    /// <summary>
    /// Registers a listener and marks it running. Called by the host after Kestrel
    /// has bound the listener's socket; not exposed on the operator API ( is
    /// startup-config + read-only list).
    /// </summary>
    Task RegisterAsync(Listener listener, CancellationToken cancellationToken = default);

    /// <summary>All registered listeners, ordered by creation time.</summary>
    Task<IReadOnlyList<Listener>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The listener, or null when unknown.</summary>
    Task<Listener?> FindAsync(ListenerId listener, CancellationToken cancellationToken = default);

    /// <summary>
    /// Repoints the listener's public endpoint -- the redirector or host-header
    /// implants dial -- without touching its bound socket (architecture.md
    /// Sec 7/8). The registry is repopulated at startup from configuration,
    /// so the mapping is fixed once the host binds; a repoint swaps the public
    /// endpoint the listener reports (severing a burned redirector). Returns null
    /// when the listener is unknown.
    /// </summary>
    Task<Listener?> RepointAsync(
        ListenerId listener,
        string publicEndpoint,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a listener from the registry -- the runtime-delete counterpart
    /// to <see cref="RegisterAsync"/>. The caller unbinds the listener's
    /// socket (or hands it to the transport that owns it); the registry only
    /// stops reporting it. Returns false when the listener is unknown.
    /// </summary>
    Task<bool> RemoveAsync(ListenerId listener, CancellationToken cancellationToken = default);

    /// <summary>
    /// The HTTP-shaped listener bound to the local port a request arrived on,
    /// or null when none matches (the request rode a socket the registry does
    /// not know, or a stream transport). This is how enrollment finds the
    /// engagement-scoped listener that received it -- the scope check compares
    /// the listener's engagement against the presented token's.
    /// </summary>
    Task<Listener?> FindByLocalPortAsync(int port, CancellationToken cancellationToken = default);
}
