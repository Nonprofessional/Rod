using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using Rod.CoreState.Listeners;
using Rod.CoreState.Transports;
using KestrelClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode;

namespace Rod.Transport.Listeners.Providers;

/// <summary>
/// The TLS posture one HTTP-family transport binds with: the endpoint URL's
/// scheme and the per-endpoint <c>ClientCertificateMode</c> riding it. The
/// posture is data so the transport set is a registration, not a switch:
/// plain HTTP carries no TLS, and the single-port https shape requests no
/// client certificate anywhere (the app-layer artifact key is the identity,
/// architecture.md Sec 8/9).
/// </summary>
public sealed record ListenerTlsPosture(string Scheme, string? ClientCertificateMode)
{
    /// <summary>Cleartext HTTP: the loopback dev posture.</summary>
    public static readonly ListenerTlsPosture Plain = new("http", null);

    /// <summary>TLS with no client-certificate request: the app-key identity.</summary>
    public static readonly ListenerTlsPosture ServerTls = new("https", null);
}

/// <summary>
/// The HTTP family's provider: the listener rides Kestrel's
/// endpoint-configuration reloader. The provider publishes the bind address
/// as an endpoint URL (carrying the posture's scheme and certificate mode)
/// into the push-only configuration source the host registered, and Kestrel
/// binds the socket in response; because the reloader reports failures only
/// to the log, the provider observes the outcome by probing the port before
/// reporting the listener as running, and withdraws the endpoint on a probe
/// failure so a refused create leaves nothing behind.
/// </summary>
public sealed class KestrelEndpointProvider : ITransportProvider
{
    // How long a published endpoint gets before its port must accept a
    // connection; covers the reloader's asynchronous bind.
    private static readonly TimeSpan BindProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BindProbeInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Initializes a provider serving <paramref name="transport"/> under
    /// <paramref name="posture"/>. The public endpoint defaults to the web
    /// dial; a Kestrel-riding transport whose dial is another shape (DoH
    /// answers for a DNS zone) names it.
    /// </summary>
    public KestrelEndpointProvider(
        string transport,
        ListenerTlsPosture posture,
        IReadOnlyList<string> carriers,
        PublicEndpointShape endpointShape = PublicEndpointShape.WebDial)
    {
        Transport = transport;
        Posture = posture;
        Carriers = carriers;
        EndpointShape = endpointShape;
    }

    /// <summary>The listener transport this provider serves, by wire name.</summary>
    public string Transport { get; }

    /// <summary>The TLS posture every endpoint this provider publishes carries.</summary>
    public ListenerTlsPosture Posture { get; }

    /// <summary>The dial shape this transport's public endpoint takes.</summary>
    public PublicEndpointShape EndpointShape { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Carriers { get; }

    /// <inheritdoc />
    public bool ServesNativeChannel
        => Carriers.Any(carrier => TransportCapabilities.Find(carrier).Channels == ChannelSupport.Native);

    /// <inheritdoc />
    public string PublicEndpointScheme => Posture.Scheme;

    /// <inheritdoc />
    public bool AcceptsPublicEndpoint(string text)
        => EndpointShape == PublicEndpointShape.DnsZone
            ? PublicEndpointShapes.IsDnsZone(text.Trim().TrimEnd('.'))
            : PublicEndpointShapes.IsWebDial(text);

    /// <inheritdoc />
    public string DescribePublicEndpointRule(string got)
        => EndpointShape == PublicEndpointShape.DnsZone
            ? $"Public endpoint must be the DNS zone this listener answers for (e.g. c2.example.test), got '{got}'."
            : $"Public endpoint accepts an absolute http(s) URL, a host:port pair, or a bare hostname "
                + "-- each is completed with the transport's scheme (and this listener's port for a bare "
                + $"hostname); got '{got}'.";

    /// <summary>The bind address shape is the shared host:port parse.</summary>
    public void Validate(ListenerConfig config)
        => _ = TransportHost.ParseBindAddress(config.BindAddress);

    /// <summary>The TCP reservation the shared probe window relies on.</summary>
    public void ReserveBind(ListenerConfig config)
        => ReserveTcpPort(config);

    /// <inheritdoc />
    public async Task<BoundListener> BindAsync(TransportBindContext context, CancellationToken cancellationToken)
    {
        var config = context.Config;
        var (host, port) = TransportHost.ParseBindAddress(config.BindAddress);

        var listener = Listener.Define(
            context.Id ?? ListenerId.New(), config.Name, config.Transport, config.BindAddress, config.PublicEndpoint,
            context.Clock.GetUtcNow(), config.EngagementId);

        context.Endpoints.PublishEndpoint(
            EndpointKey(listener.Id), $"{Posture.Scheme}://{config.BindAddress}", Posture.ClientCertificateMode);

        if (!await WaitForAcceptingAsync(context, host, port, cancellationToken).ConfigureAwait(false))
        {
            context.Endpoints.WithdrawEndpoint(EndpointKey(listener.Id));
            throw new InvalidOperationException(
                $"Listener '{config.Name}' could not bind {config.BindAddress}: the socket never opened "
                + "(the address is likely in use); see the teamserver log.");
        }

        await context.Registry.RegisterAsync(listener, cancellationToken).ConfigureAwait(false);
        context.Logger.LogInformation(
            "Runtime listener {Name} ({Transport}) bound on {BindAddress} for {PublicEndpoint}.",
            config.Name, config.Transport, config.BindAddress, config.PublicEndpoint);
        return new BoundListener(listener, Service: null);
    }

    // The endpoint key under Kestrel:Endpoints. The listener id, not the name:
    // names can repeat or carry characters configuration keys would rather not.
    // The manager shares it for the withdraw a removal performs.
    internal static string EndpointKey(ListenerId listener) => $"rod-{listener.Value:N}";

    // Polls until the address accepts a TCP connection, the observable form of
    // "the reloader bound it". A wildcard bind is probed on loopback -- the
    // socket any wildcard bind necessarily covers.
    private static async Task<bool> WaitForAcceptingAsync(
        TransportBindContext context, IPAddress host, int port, CancellationToken cancellationToken)
    {
        var probeHost = host.Equals(IPAddress.Any) || host.Equals(IPAddress.IPv6Any) ? IPAddress.Loopback : host;
        var deadline = context.Clock.GetUtcNow() + BindProbeTimeout;
        while (context.Clock.GetUtcNow() < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(probeHost, port, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (SocketException)
            {
                // Not accepting yet; retry after the interval.
            }
            await Task.Delay(BindProbeInterval, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    // Reserves the bind's port exclusively for a moment, so a bind that would
    // collide with a live socket is refused up front -- the reloader reports
    // its bind failures only to the log, and nothing downstream could tell
    // the create from a silent no-op. The moment between release and the real
    // bind is the usual check-then-act window; operator tooling, not a lock,
    // is the right answer at this layer.
    internal static void ReserveTcpPort(ListenerConfig config)
    {
        var (host, port) = TransportHost.ParseBindAddress(config.BindAddress);
        var reserveHost = host.Equals(IPAddress.Any) || host.Equals(IPAddress.IPv6Any) ? IPAddress.Loopback : host;
        var reserve = new TcpListener(reserveHost, port);
        try
        {
            reserve.Start();
        }
        catch (SocketException)
        {
            throw new InvalidOperationException(
                $"Listener '{config.Name}' could not bind {config.BindAddress}: the address is already in use.");
        }
        finally
        {
            reserve.Stop();
        }
    }
}
