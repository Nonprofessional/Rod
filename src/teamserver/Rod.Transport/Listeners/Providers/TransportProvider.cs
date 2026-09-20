using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rod.CoreState.Listeners;

namespace Rod.Transport.Listeners.Providers;

/// <summary>
/// One transport's bind behavior (architecture.md Sec 8): everything a
/// runtime listener of that transport needs to open its socket, expressed as
/// one object instead of a switch arm in <c>ListenerManager</c>. The two
/// shapes the in-tree transports use are shared implementations --
/// <see cref="KestrelEndpointProvider"/> (the HTTP family rides Kestrel's
/// endpoint reloader) and <see cref="HostedServiceTransportProvider"/> (the
/// socket-owning transports run a per-listener hosted service) -- and a
/// transport that fits neither implements this interface directly. The
/// registry (<see cref="TransportProviders"/>) maps the transport's wire
/// name to its provider, so a transport added later registers itself rather
/// than editing an enumeration and every switch over it.
/// </summary>
public interface ITransportProvider
{
    /// <summary>The listener transport this provider serves, by wire name.</summary>
    string Transport { get; }

    /// <summary>
    /// The contact carriers this transport serves, by their wire names (the
    /// registry the core-state capability table keys). The build pipeline
    /// reads this to decide whether a listener may be named as a beacon -- a
    /// carrier with native channel support makes it claimable -- and the
    /// capability story stays one table's answer instead of a second
    /// transport-to-carrier map somewhere else.
    /// </summary>
    IReadOnlyList<string> Carriers { get; }

    /// <summary>
    /// Whether one of this transport's carriers holds a live stream (native
    /// channel support): the listener may be named as a build's beacon.
    /// </summary>
    bool ServesNativeChannel { get; }

    /// <summary>
    /// The scheme a public endpoint completes with when the operator supplies
    /// a bare host or host:port ("http" or "https" for the URL-dialing
    /// family). The socket-owning transports never dial URLs, but their
    /// endpoints still complete with the TLS default the HTTP family's
    /// non-plain members use.
    /// </summary>
    string PublicEndpointScheme { get; }

    /// <summary>
    /// Whether <paramref name="text"/> is an acceptable public endpoint for
    /// this transport -- the address implants are told to dial, whose shape
    /// the transport defines (an absolute http(s) URL or host:port for the
    /// web family, a zone, a pipe path, a host:port for the socket-owning
    /// ones).
    /// </summary>
    bool AcceptsPublicEndpoint(string text);

    /// <summary>
    /// One sentence naming the public endpoint shapes this transport accepts,
    /// for a refusal that teaches. <paramref name="got"/> is the rejected
    /// value.
    /// </summary>
    string DescribePublicEndpointRule(string got);

    /// <summary>
    /// Rejects a malformed bind address for this transport's shape. Throws
    /// <see cref="InvalidOperationException"/> -- the caller maps it to the
    /// 400-class "operator mistake" refusal.
    /// </summary>
    void Validate(ListenerConfig config);

    /// <summary>
    /// Reserves the bind's port exclusively for a moment where the transport
    /// uses one, so a colliding create is refused up front (the 409-class
    /// refusal) instead of binding silently over a live socket or failing
    /// only to a log. Transports with no port to reserve (a named pipe) do
    /// nothing.
    /// </summary>
    void ReserveBind(ListenerConfig config);

    /// <summary>
    /// Binds the listener, registers it once its socket is observably up, and
    /// returns it with the hosted service that owns its socket (null when the
    /// socket belongs to the host -- the Kestrel-reloader shape). Throws
    /// <see cref="InvalidOperationException"/> when the bind is refused.
    /// </summary>
    Task<BoundListener> BindAsync(TransportBindContext context, CancellationToken cancellationToken);
}

/// <summary>A completed bind: the registered listener and its socket owner.</summary>
public sealed record BoundListener(Listener Listener, IHostedService? Service);

/// <summary>
/// Everything a provider's bind needs beyond its configuration: the id a
/// restore carries (null mints a fresh one on create), the host's service
/// provider for the collaborators a bind composes, the listener registry the
/// bind registers into, the Kestrel endpoint publisher the reloader shape
/// writes, and the clock and logger every bind path shares.
/// </summary>
public sealed record TransportBindContext(
    ListenerConfig Config,
    ListenerId? Id,
    IServiceProvider Services,
    IListenerRegistry Registry,
    DynamicEndpointsConfiguration Endpoints,
    TimeProvider Clock,
    ILogger Logger);

/// <summary>
/// Constructs a listener's hosted service: the socket-owning transports'
/// factory, handed the host's services, the registry the service registers
/// into, and the listener it serves.
/// </summary>
public delegate IHostedService ListenerServiceFactory(
    IServiceProvider services,
    IListenerRegistry registry,
    Listener listener);
