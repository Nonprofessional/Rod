using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rod.Audit;
using Rod.CoreState.Application;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.CoreState.Live;
using Rod.CoreState.Sessions;
using Rod.CoreState.ShellSessions;
using Rod.CoreState.Tasks;
using Rod.CoreState.Transports;
using Rod.Transport.Channels;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners.Dns;
using Rod.Transport.Listeners.ShellCatch;
using Rod.Transport.Listeners.Streams;

namespace Rod.Transport.Listeners.Providers;

/// <summary>
/// The transport-to-provider registry: the wire name a listener names, the
/// provider that binds it. The in-tree transports register themselves in the
/// static constructor -- the HTTP family as one Kestrel-publication shape
/// under its TLS postures, the socket-owning family as hosted-service
/// shapes -- and a transport arriving later registers the same way instead
/// of editing an enumeration and every switch over it. The same
/// open-registration shape the carrier capability table uses on the
/// core-state side (<c>TransportCapabilities</c>): name-keyed,
/// conflict-refusing, and conservative toward what never registered.
/// </summary>
public static class TransportProviders
{
    private static readonly ConcurrentDictionary<string, ITransportProvider> Providers =
        new(StringComparer.OrdinalIgnoreCase);

    static TransportProviders()
    {
        // The HTTP family: one publication shape, two TLS postures -- the
        // plain loopback dev posture and the single-port app-key https shape
        // (architecture.md Sec 8/9). Every web front serves the envelope
        // carrier and the WebSocket beacon (the stream-mode web build dials
        // it) -- both native channel carriers.
        Register(new KestrelEndpointProvider("http", ListenerTlsPosture.Plain,
            new[] { TransportCapabilities.EnvelopeName, TransportCapabilities.BeaconStreamName }));
        Register(new KestrelEndpointProvider("https", ListenerTlsPosture.ServerTls,
            new[] { TransportCapabilities.EnvelopeName, TransportCapabilities.BeaconStreamName }));

        // The socket-owning family: a datagram reservation for DNS, a stream
        // reservation for the raw socket -- and each its own public endpoint
        // dial shape (a zone, a host:port).
        Register(new HostedServiceTransportProvider("dns",
            new HostedBindShape(BindReservation.UdpPort, PublicEndpointShape.DnsZone),
            new[] { TransportCapabilities.DnsName },
            (services, registry, listener) => new DnsListenerService(
                listener,
                services.GetRequiredService<DnsBeaconBridge>(),
                registry,
                services.GetRequiredService<ILoggerFactory>().CreateLogger<DnsListenerService>())));
        Register(new HostedServiceTransportProvider("tcp",
            new HostedBindShape(BindReservation.TcpPort, PublicEndpointShape.HostPort),
            new[] { TransportCapabilities.MessagePipeName },
            (services, registry, listener) => new TcpListenerService(
                listener,
                services.GetRequiredService<StreamBeaconBridge>(),
                registry,
                services.GetRequiredService<ILoggerFactory>().CreateLogger<TcpListenerService>())));

        // The DNS grammar's second carriage (RFC 8484): the same TXT
        // contact wire the UDP listener answers, as DNS wire messages over
        // an HTTPS body -- the egress-restricted carrier behind a shape a
        // restricted network already allows. The single-port TLS posture
        // (no client certificate anywhere); the public endpoint is the zone
        // it answers for, the UDP listener's model.
        Register(new KestrelEndpointProvider("doh", ListenerTlsPosture.ServerTls,
            new[] { TransportCapabilities.DnsName }, PublicEndpointShape.DnsZone));

        // The stream family's catcher: a TCP socket that holds connections
        // speaking no Rod protocol at all (architecture.md Sec 8) -- the
        // reverse shells an operator's one-liners dial home over. It serves
        // no contact carrier (nothing here is implant ingress, so a build
        // may never name it as a beacon), and its public endpoint is the
        // bare host:port the one-liners dial, the family's dial shape.
        Register(new HostedServiceTransportProvider("shellcatch",
            new HostedBindShape(BindReservation.TcpPort, PublicEndpointShape.HostPort),
            Array.Empty<string>(),
            (services, registry, listener) => new ShellCatchListenerService(
                listener,
                services.GetRequiredService<ShellCatchHub>(),
                services.GetRequiredService<IShellSessionRegistry>(),
                services.GetRequiredService<ILiveEventBus>(),
                services.GetRequiredService<IAuditStore>(),
                registry,
                services.GetRequiredService<TimeProvider>(),
                services.GetRequiredService<ILoggerFactory>().CreateLogger<ShellCatchListenerService>())));
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
    /// when nothing registered it (including no name at all) -- the caller
    /// decides whether that is a refusal (a create naming an unknown
    /// transport) or a skip (a stored definition that predates the registry).
    /// </summary>
    public static ITransportProvider? Find(string? transport)
        => !string.IsNullOrWhiteSpace(transport) && Providers.TryGetValue(transport, out var found)
            ? found
            : null;

    /// <summary>
    /// Every registered transport's wire name, sorted: the set an operator
    /// may name on a create, for refusal messages and listings alike.
    /// </summary>
    public static IReadOnlyList<string> Names()
        => Providers.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
}
