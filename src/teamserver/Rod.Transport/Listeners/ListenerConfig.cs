namespace Rod.Transport.Listeners;

/// <summary>
/// One entry in the teamserver's startup listener configuration (architecture.md
/// Sec 8). The host binds one socket per entry via <c>UseRodListeners</c>. This is
/// configuration shape only -- it is not a <see cref="Listener"/> until the host
/// has bound it and registered the result.
/// </summary>
public sealed record ListenerConfig(
    string Name,
    ListenerTransport Transport,
    string BindAddress,
    string PublicEndpoint);
