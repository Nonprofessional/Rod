using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rod.CoreState.Listeners;

namespace Rod.Transport.Listeners.Providers;

/// <summary>How a socket-owning transport's bind reserves its port.</summary>
public enum BindReservation
{
    /// <summary>No port to reserve: the bind address is a bare pipe name.</summary>
    None,

    /// <summary>A UDP socket's port: the datagram reservation.</summary>
    UdpPort,

    /// <summary>A TCP socket's port: the stream reservation.</summary>
    TcpPort,
}

/// <summary>
/// The address shape and port reservation a socket-owning transport binds
/// with: whether the bind address is a bare pipe name (validated as one,
/// reserved not at all) or a host:port pair (validated as one, reserved over
/// UDP or TCP per the socket the transport opens).
/// </summary>
public sealed record HostedBindShape(BindReservation Reservation, bool BarePipeName);

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
    // UDP listener and the pipe server both come up well inside it.
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RegistrationPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly HostedBindShape _shape;
    private readonly ListenerServiceFactory _factory;

    /// <summary>Initializes a provider serving <paramref name="transport"/> with the given shape and service factory.</summary>
    public HostedServiceTransportProvider(string transport, HostedBindShape shape, ListenerServiceFactory factory)
    {
        Transport = transport;
        _shape = shape;
        _factory = factory;
    }

    /// <summary>The listener transport this provider serves, by wire name.</summary>
    public string Transport { get; }

    /// <summary>
    /// Rejects a malformed bind address: a pipe-named transport refuses a
    /// host:port pair, everything else must parse as one.
    /// </summary>
    public void Validate(ListenerConfig config)
    {
        if (_shape.BarePipeName)
        {
            // A bare pipe name; the pipe server creates it on bind.
            if (config.BindAddress.Contains(':', StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"SMB bind address '{config.BindAddress}' is a bare pipe name, not host:port.");
            return;
        }

        _ = TransportHost.ParseBindAddress(config.BindAddress);
    }

    /// <summary>Reserves per the shape: nothing for a pipe, a UDP or TCP port otherwise.</summary>
    public void ReserveBind(ListenerConfig config)
    {
        switch (_shape.Reservation)
        {
            case BindReservation.None:
                return;
            case BindReservation.UdpPort:
                {
                    var (host, port) = TransportHost.ParseBindAddress(config.BindAddress);
                    using var udp = new UdpClient(new IPEndPoint(host, port));
                    return;
                }
            default:
                KestrelEndpointProvider.ReserveTcpPort(config);
                return;
        }
    }

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
