using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rod.Transport.Listeners.Dns;
using Rod.Transport.Listeners.Streams;

namespace Rod.Transport.Listeners.Providers;

/// <summary>
/// The transport-to-provider registry: the wire name a listener names, the
/// provider that binds it. The in-tree six register themselves in the static
/// constructor -- the HTTP family as one Kestrel-publication shape under its
/// three TLS postures, the socket-owning three as hosted-service shapes --
/// and a transport arriving later registers the same way instead of editing
/// an enumeration and every switch over it. The same open-registration shape
/// the carrier capability table uses on the core-state side
/// (<c>TransportCapabilities</c>): name-keyed, conflict-refusing, and
/// conservative toward what never registered.
/// </summary>
public static class TransportProviders
{
    private static readonly ConcurrentDictionary<string, ITransportProvider> Providers =
        new(StringComparer.OrdinalIgnoreCase);

    static TransportProviders()
    {
        // The HTTP family: one publication shape, three TLS postures -- the
        // plain loopback dev posture, the single-port app-key https shape,
        // and the client-certificate-asking mTLS shape (architecture.md
        // Sec 8/9).
        Register(new KestrelEndpointProvider("http", ListenerTlsPosture.Plain));
        Register(new KestrelEndpointProvider("https", ListenerTlsPosture.ServerTls));
        Register(new KestrelEndpointProvider("mtls", ListenerTlsPosture.MutualAsk));

        // The socket-owning family: a datagram reservation and a bare pipe
        // name's absence of one, per each transport's socket.
        Register(new HostedServiceTransportProvider("dns",
            new HostedBindShape(BindReservation.UdpPort, BarePipeName: false),
            (services, registry, listener) => new DnsListenerService(
                listener,
                services.GetRequiredService<DnsBeaconBridge>(),
                registry,
                services.GetRequiredService<ILoggerFactory>().CreateLogger<DnsListenerService>())));
        Register(new HostedServiceTransportProvider("smb",
            new HostedBindShape(BindReservation.None, BarePipeName: true),
            (services, registry, listener) => new SmbListenerService(
                listener,
                services.GetRequiredService<StreamBeaconBridge>(),
                registry,
                services.GetRequiredService<ILoggerFactory>().CreateLogger<SmbListenerService>())));
        Register(new HostedServiceTransportProvider("tcp",
            new HostedBindShape(BindReservation.TcpPort, BarePipeName: false),
            (services, registry, listener) => new TcpListenerService(
                listener,
                services.GetRequiredService<StreamBeaconBridge>(),
                registry,
                services.GetRequiredService<ILoggerFactory>().CreateLogger<TcpListenerService>())));
    }

    /// <summary>
    /// Registers a provider under its transport's wire name. Registering the
    /// same instance again is a no-op; a different provider under a live name
    /// throws, because silently replacing a transport's bind behavior would
    /// hide the mistake until a listener fails to open.
    /// </summary>
    public static void Register(ITransportProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.Transport))
            throw new ArgumentException("A transport wire name is required.", nameof(provider));

        var existing = Providers.GetOrAdd(provider.Transport, provider);
        if (!ReferenceEquals(existing, provider))
            throw new InvalidOperationException(
                $"The transport '{provider.Transport}' is already served by a different provider.");
    }

    /// <summary>
    /// The provider serving <paramref name="transport"/>'s wire name, or null
    /// when nothing registered it -- the caller decides whether that is a
    /// refusal (a create naming an unknown transport) or a skip (a stored
    /// definition that predates the registry).
    /// </summary>
    public static ITransportProvider? Find(string transport)
        => !string.IsNullOrWhiteSpace(transport) && Providers.TryGetValue(transport, out var found)
            ? found
            : null;
}
