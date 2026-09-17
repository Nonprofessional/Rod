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
using Rod.Transport.Listeners.Quic;
using Rod.Transport.Listeners.ShellCatch;
using Rod.Transport.Listeners.Streams;

namespace Rod.Transport.Listeners.Providers;

/// <summary>
/// The transport-to-provider registry: the wire name a listener names, the
/// provider that binds it. The in-tree seven register themselves in the
/// static constructor -- the HTTP family as one Kestrel-publication shape
/// under its three TLS postures, the socket-owning four as hosted-service
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
        // The HTTP family: one publication shape, three TLS postures -- the
        // plain loopback dev posture, the single-port app-key https shape,
        // and the client-certificate-asking mTLS shape (architecture.md
        // Sec 8/9). Every web front serves the envelope carrier; the
        // stream-mode web build dials the WebSocket beacon, so the web
        // fronts carry the beacon-stream carrier too, alongside the mTLS
        // shape's gRPC stream -- both native channel carriers.
        Register(new KestrelEndpointProvider("http", ListenerTlsPosture.Plain,
            new[] { TransportCapabilities.EnvelopeName, TransportCapabilities.BeaconStreamName }));
        Register(new KestrelEndpointProvider("https", ListenerTlsPosture.ServerTls,
            new[] { TransportCapabilities.EnvelopeName, TransportCapabilities.BeaconStreamName }));
        Register(new KestrelEndpointProvider("mtls", ListenerTlsPosture.MutualAsk,
            new[] { TransportCapabilities.BeaconStreamName, TransportCapabilities.EnvelopeName }));

        // The socket-owning family: a datagram reservation and a bare pipe
        // name's absence of one, per each transport's socket -- and each its
        // own public endpoint dial shape (a zone, a pipe path, a host:port).
        Register(new HostedServiceTransportProvider("dns",
            new HostedBindShape(BindReservation.UdpPort, BarePipeName: false, PublicEndpointShape.DnsZone),
            new[] { TransportCapabilities.DnsName },
            (services, registry, listener) => new DnsListenerService(
                listener,
                services.GetRequiredService<DnsBeaconBridge>(),
                registry,
                services.GetRequiredService<ILoggerFactory>().CreateLogger<DnsListenerService>())));
        Register(new HostedServiceTransportProvider("smb",
            new HostedBindShape(BindReservation.None, BarePipeName: true, PublicEndpointShape.PipePath),
            new[] { TransportCapabilities.MessagePipeName },
            (services, registry, listener) => new SmbListenerService(
                listener,
                services.GetRequiredService<StreamBeaconBridge>(),
                registry,
                services.GetRequiredService<ILoggerFactory>().CreateLogger<SmbListenerService>())));
        Register(new HostedServiceTransportProvider("tcp",
            new HostedBindShape(BindReservation.TcpPort, BarePipeName: false, PublicEndpointShape.HostPort),
            new[] { TransportCapabilities.MessagePipeName },
            (services, registry, listener) => new TcpListenerService(
                listener,
                services.GetRequiredService<StreamBeaconBridge>(),
                registry,
                services.GetRequiredService<ILoggerFactory>().CreateLogger<TcpListenerService>())));

        // The socket-owning family's duplex variant: a QUIC listener over the
        // UDP reservation, for egress that passes UDP/443 but blocks TCP. The
        // public endpoint stays the bare host:port the family dials; the
        // scheme the transport completes it with is its own (quic://), the
        // URL shape the artifact's check-in client picks by. The carrier is
        // the native stream carrier: one connection is one live session
        // (server-push tasking, live channels), not the family's poll cycle.
        // The opening stream also carries enrollment (Sec 8, enrollment over
        // QUIC), so the service drives the same shared enrollment flow the
        // web route drives, scoped by the listener's own engagement.
        Register(new HostedServiceTransportProvider("quic",
            new HostedBindShape(BindReservation.UdpPort, BarePipeName: false, PublicEndpointShape.HostPort),
            new[] { TransportCapabilities.BeaconStreamName },
            (services, registry, listener) => new QuicListenerService(
                listener,
                services.GetRequiredService<HandshakeService>(),
                services.GetRequiredService<ISessionRegistry>(),
                services.GetRequiredService<TaskService>(),
                services.GetRequiredService<IAuditStore>(),
                services.GetRequiredService<TimeProvider>(),
                services.GetRequiredService<ITaskDispatchWake>(),
                services.GetRequiredService<LiveChannelHub>(),
                services.GetRequiredService<TaskRelayHub>(),
                services.GetRequiredService<SocksProxyHub>(),
                services.GetRequiredService<BeaconIngest>(),
                services.GetRequiredService<BeaconTasking>(),
                services.GetRequiredService<IImplantCertificateAuthority>(),
                registry,
                services.GetRequiredService<EnrollmentService>(),
                services.GetRequiredService<Rod.CoreState.Staging.IStagerTokenService>(),
                services.GetRequiredService<IPayloadStore>(),
                services.GetRequiredService<Endpoints.EnvelopeCheckInKeys>(),
                services.GetRequiredService<ILoggerFactory>().CreateLogger<QuicListenerService>()),
            publicEndpointScheme: "quic"));

        // The DNS grammar's second carriage (RFC 8484): the same TXT
        // check-in wire the UDP listener answers, as DNS wire messages over
        // an HTTPS body -- the egress-restricted carrier behind a shape a
        // restricted network already allows. The single-port TLS posture
        // (no client certificate anywhere); the public endpoint is the zone
        // it answers for, the UDP listener's model.
        Register(new KestrelEndpointProvider("doh", ListenerTlsPosture.ServerTls,
            new[] { TransportCapabilities.DnsName }, PublicEndpointShape.DnsZone));

        // The stream family's catcher: a TCP socket that holds connections
        // speaking no Rod protocol at all (architecture.md Sec 8) -- the
        // reverse shells an operator's one-liners dial home over. It serves
        // no check-in carrier (nothing here is implant ingress, so a build
        // may never name it as a beacon), and its public endpoint is the
        // bare host:port the one-liners dial, the family's dial shape.
        Register(new HostedServiceTransportProvider("shellcatch",
            new HostedBindShape(BindReservation.TcpPort, BarePipeName: false, PublicEndpointShape.HostPort),
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
