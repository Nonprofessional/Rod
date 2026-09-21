using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rod.CoreState.Listeners;
using Rod.CoreState.Transports;

namespace Rod.Transport.Listeners.Providers;

/// <summary>How a socket-owning transport's bind reserves its port.</summary>
public enum BindReservation
{
    /// <summary>A UDP socket's port: the datagram reservation.</summary>
    UdpPort,

    /// <summary>A TCP socket's port: the stream reservation.</summary>
    TcpPort,
}

/// <summary>The dial shape a transport's public endpoint takes.</summary>
public enum PublicEndpointShape
{
    /// <summary>
    /// An absolute http(s) URL, host:port pair, or bare hostname -- the web
    /// family's dial, completed with the transport's scheme.
    /// </summary>
    WebDial,

    /// <summary>A DNS zone the listener answers for (e.g. c2.example.test).</summary>
    DnsZone,

    /// <summary>The host:port implants dial (e.g. 203.0.113.10:443).</summary>
    HostPort,
}

/// <summary>
/// The address shape and port reservation a socket-owning transport binds
/// with: the host:port bind address (validated as one, reserved over UDP or
/// TCP per the socket the transport opens), and the dial shape its public
/// endpoint takes.
/// </summary>
public sealed record HostedBindShape(
    BindReservation Reservation,
    PublicEndpointShape EndpointShape);

/// <summary>
/// The socket-owning family's provider: each listener runs as a per-listener
/// hosted service the provider starts on demand -- the same services the
/// startup path registers -- constructed by the transport's factory. The
/// service binds its own socket and registers the listener into the registry
/// itself; the provider waits for that registration (bind-then-register, the
/// shape every transport follows) so the create answer means listening, not
/// "scheduled to listen", and stops the service quietly when the deadline
/// passes so a failed create leaves nothing behind.
/// </summary>
public sealed class HostedServiceTransportProvider : ITransportProvider
{
    // How long a started service gets to register its bound socket; the
    // UDP listener and the TCP listener both come up well inside it.
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RegistrationPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly HostedBindShape _shape;
    private readonly string _publicEndpointScheme;
    private readonly ListenerServiceFactory _factory;

    /// <summary>
    /// Initializes a provider serving <paramref name="transport"/> with the
    /// given shape, carriers, and service factory. The public endpoint scheme
    /// defaults to the TLS shape the HTTP family's non-plain members use; a
    /// transport whose dial is a scheme of its own names it, and the operator
    /// surface completes bare endpoints with it.
    /// </summary>
    public HostedServiceTransportProvider(
        string transport,
        HostedBindShape shape,
        IReadOnlyList<string> carriers,
        ListenerServiceFactory factory,
        string? publicEndpointScheme = null)
    {
        Transport = transport;
        _shape = shape;
        Carriers = carriers;
        _publicEndpointScheme = publicEndpointScheme ?? "https";
        _factory = factory;
    }

    /// <summary>The listener transport this provider serves, by wire name.</summary>
    public string Transport { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Carriers { get; }

    /// <inheritdoc />
    public bool ServesNativeChannel
        => Carriers.Any(carrier => TransportCapabilities.Find(carrier).Channels == ChannelSupport.Native);

    /// <inheritdoc />
    public string PublicEndpointScheme => _publicEndpointScheme;

    /// <inheritdoc />
    public bool AcceptsPublicEndpoint(string text)
    {
        var value = text.Trim().TrimEnd('.');
        if (value.Length == 0)
            return false;
        return _shape.EndpointShape switch
        {
            PublicEndpointShape.DnsZone => PublicEndpointShapes.IsDnsZone(value),
            _ => PublicEndpointShapes.IsSocketDial(value),
        };
    }

    /// <inheritdoc />
    public string DescribePublicEndpointRule(string got) => _shape.EndpointShape switch
    {
        PublicEndpointShape.DnsZone => $"Public endpoint must be the DNS zone this listener answers for (e.g. c2.example.test), got '{got}'.",
        _ => $"Public endpoint must be the host:port implants dial (e.g. 203.0.113.10:443), got '{got}'.",
    };

    /// <summary>
    /// Rejects a malformed bind address: it must parse as a host:port pair.
    /// </summary>
    public void Validate(ListenerConfig config)
    {
        _ = TransportHost.ParseBindAddress(config.BindAddress);
    }

    /// <summary>Reserves per the shape: a UDP or TCP port.</summary>
    public void ReserveBind(ListenerConfig config)
    {
        switch (_shape.Reservation)
        {
            case BindReservation.UdpPort:
                {
                    // Wrapped the way the runtime bind wraps: a raw
                    // SocketException (address not on this host, port in use,
                    // privileged port) escapes the create endpoint's handler
                    // as a 500 instead of the bind-refused conflict its
                    // InvalidOperationException promises.
                    var (host, port) = TransportHost.ParseBindAddress(config.BindAddress);
                    try
                    {
                        using var udp = new UdpClient(new IPEndPoint(host, port));
                    }
                    catch (SocketException ex)
                    {
                        throw new InvalidOperationException(
                            $"Listener '{config.Name}' could not bind {config.BindAddress}: {ex.Message}");
                    }
                    return;
                }
            default:
                KestrelEndpointProvider.ReserveTcpPort(config);
                return;
        }
    }

    /// <summary>
    /// Builds one listener's hosted service -- the construction the runtime
    /// bind performs and the startup path registers share, so a transport's
    /// service wiring lives in exactly one place.
    /// </summary>
    public IHostedService CreateService(
        IServiceProvider services, IListenerRegistry registry, Listener listener)
        => _factory(services, registry, listener);

    /// <inheritdoc />
    public async Task<BoundListener> BindAsync(TransportBindContext context, CancellationToken cancellationToken)
    {
        var config = context.Config;
        var listener = Listener.Define(
            context.Id ?? ListenerId.New(), config.Name, config.Transport, config.BindAddress, config.PublicEndpoint,
            context.Clock.GetUtcNow(), config.EngagementId);

        var service = _factory(context.Services, context.Registry, listener);
        try
        {
            await service.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                ex, "Runtime listener '{Name}' failed to bind {BindAddress}.", config.Name, config.BindAddress);
            throw new InvalidOperationException(
                $"Listener '{config.Name}' could not bind {config.BindAddress}: {ex.Message}");
        }

        // The service registers itself into the registry once its socket is
        // bound; the create answer waits for that registration so "running"
        // means listening, not "scheduled to listen".
        var registrationDeadline = context.Clock.GetUtcNow() + RegistrationTimeout;
        while (await context.Registry.FindAsync(listener.Id, cancellationToken).ConfigureAwait(false) is null)
        {
            if (context.Clock.GetUtcNow() > registrationDeadline)
            {
                await StopQuietlyAsync(service).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Listener '{config.Name}' could not bind {config.BindAddress}: the service never registered its socket.");
            }
            await Task.Delay(RegistrationPollInterval, cancellationToken).ConfigureAwait(false);
        }

        context.Logger.LogInformation(
            "Runtime listener {Name} ({Transport}) bound on {BindAddress} for {PublicEndpoint}.",
            config.Name, config.Transport, config.BindAddress, config.PublicEndpoint);
        return new BoundListener(listener, service);
    }

    private static async Task StopQuietlyAsync(IHostedService service)
    {
        try
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the service never served; the create is refused
            // regardless.
        }
    }
}
