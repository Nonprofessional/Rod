using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rod.CoreState.Engagements;
using Rod.CoreState.Listeners;
using Rod.CoreState.Pki;
using Rod.Transport.Listeners.Dns;
using Rod.Transport.Listeners.Streams;

namespace Rod.Transport.Listeners;

/// <summary>
/// Runtime listener management: create and remove C2 ingress while the
/// teamserver serves, the operator-API counterpart to the startup
/// <c>Listeners</c> configuration (architecture.md Sec 8). Two mechanics sit
/// behind one shape:
///
/// - The HTTP-shaped transports (<c>Http</c>, <c>Mtls</c>, <c>HttpsEnvelope</c>)
///   ride Kestrel's endpoint-configuration reloader: the manager publishes the
///   bind address as an endpoint URL into a push-only configuration source the
///   host registered with <c>KestrelServerOptions.Configure(..., reloadOnChange:
///   true)</c>, and Kestrel binds (create) or drains and unbinds (remove) the
///   socket in response. A create confirms the bind by probing the port before
///   reporting the listener as running -- the reloader's errors are logs, not
///   exceptions, so the manager observes the outcome instead of assuming it.
///
/// - The stream transports (<c>Dns</c>, <c>Smb</c>, <c>Tcp</c>) own their
///   sockets in per-listener hosted services; the manager starts and stops one
///   service per listener on demand, the same services the startup path
///   registers.
///
/// Runtime listeners are engagement-scoped: each create names the engagement
/// the listener answers for, and its definition is persisted once the socket
/// binds, so a restart rebinds what the operator built (the restore keeps the
/// listener's id). Removing a startup-bound listener is refused (the reloader
/// only touches endpoints it owns); the configuration is the owner of those,
/// and they carry no engagement -- the shared tier.
/// </summary>
public sealed class ListenerManager
{
    // How long a created HTTP-shaped endpoint gets before its port must accept
    // a connection; covers the reloader's asynchronous bind.
    private static readonly TimeSpan BindProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BindProbeInterval = TimeSpan.FromMilliseconds(100);

    private readonly IServiceProvider _services;
    private readonly IListenerRegistry _listeners;
    private readonly IListenerStore _definitions;
    private readonly IEngagementRepository _engagements;
    private readonly TimeProvider _clock;
    private readonly ILogger<ListenerManager> _logger;
    private readonly DynamicEndpointsConfiguration _endpoints = new();
    private readonly ConcurrentDictionary<ListenerId, RuntimeEntry> _runtime = new();

    private sealed record RuntimeEntry(Listener Listener, IHostedService? Service);

    public ListenerManager(
        IServiceProvider services,
        IListenerRegistry listeners,
        IListenerStore definitions,
        IEngagementRepository engagements,
        TimeProvider clock,
        ILogger<ListenerManager> logger)
    {
        _services = services;
        _listeners = listeners;
        _definitions = definitions;
        _engagements = engagements;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The Kestrel section carrying runtime endpoint entries; the host hands it to <c>Configure</c> with reload on change.</summary>
    internal IConfiguration KestrelSection => _endpoints.KestrelSection;

    /// <summary>True when the listener was created at runtime through this manager.</summary>
    public bool IsRuntime(ListenerId listener)
        => _runtime.ContainsKey(listener);

    /// <summary>
    /// The HTTPS defaults for runtime-created TLS endpoints -- the termination
    /// every transport shares: the CA-issued server leaf, and chain-to-CA
    /// validation for whatever client certificate a transport asks to see.
    /// The certificate mode is deliberately not set here:
    /// <c>ConfigureHttpsDefaults</c> runs for every endpoint alike and would
    /// flatten the one HTTPS knob the transports differ on, so each published
    /// endpoint carries its own per-endpoint <c>ClientCertificateMode</c> in
    /// the configuration (see <see cref="CreateHttpListenerAsync"/>): the
    /// mTLS-shaped listeners request the certificate, and every other TLS
    /// endpoint -- the https transport's fingerprint rule, no request at all
    /// -- leaves it at the Kestrel default.
    /// </summary>
    internal void ApplyDynamicHttpsDefaults(HttpsConnectionAdapterOptions https)
    {
        https.ServerCertificateSelector = (_, _) =>
            _services.GetRequiredService<IImplantCertificateAuthority>().GetServerCertificate();
        https.ClientCertificateValidation = (certificate, chain, _) =>
            TransportHost.ClientCertificateChainsToCa(certificate, chain, _services);
        https.CheckCertificateRevocation = false;
    }

    /// <summary>
    /// Creates and binds an engagement-scoped listener and persists its
    /// definition, so a restart rebinds it. Returns the registered (running)
    /// listener. Throws <see cref="ArgumentException"/> for a malformed
    /// request (bad bind address for the transport, missing or unknown
    /// engagement) and <see cref="InvalidOperationException"/> when the bind is
    /// refused (port in use) -- the caller maps those to 400 and 409.
    /// </summary>
    public async Task<Listener> CreateAsync(ListenerConfig config, CancellationToken cancellationToken = default)
    {
        if (config.EngagementId is not { } engagementId)
            throw new ArgumentException(
                "A runtime listener belongs to one engagement; supply its id.", nameof(config));

        var engagement = await _engagements.FindAsync(engagementId, cancellationToken);
        if (engagement is null)
            throw new ArgumentException(
                $"Engagement {engagementId} does not exist.", nameof(config));

        var listener = await BindAsync(config, id: null, cancellationToken);

        // Persist only once the socket is bound: a definition for a listener
        // that never opened would resurrect as a phantom on every restart.
        await _definitions.SaveAsync(DefinitionOf(listener), cancellationToken);
        return listener;
    }

    /// <summary>
    /// Rebinds a persisted definition at startup: the same bind path as
    /// <see cref="CreateAsync"/> with the definition's own id, and no re-save
    /// (the record is already the truth being restored). A definition whose
    /// port no longer binds is logged and skipped -- the roster shows what is
    /// actually listening -- instead of failing the whole boot.
    /// </summary>
    public async Task<Listener?> RestoreAsync(ListenerDefinition definition, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ListenerTransport>(definition.Transport, ignoreCase: true, out var transport))
        {
            _logger.LogWarning(
                "Stored listener {ListenerId} carries unknown transport '{Transport}'; skipped.",
                definition.Id, definition.Transport);
            return null;
        }

        try
        {
            var config = new ListenerConfig(
                definition.Name, transport, definition.BindAddress, definition.PublicEndpoint, definition.EngagementId);
            return await BindAsync(config, new ListenerId(definition.Id), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Stored listener {ListenerId} ({Name}) could not rebind {BindAddress}; skipped. "
                + "Free the port or delete the listener and recreate it.",
                definition.Id, definition.Name, definition.BindAddress);
            return null;
        }
    }

    // The shared bind path: validate, reserve the port, bind per transport
    // shape, and register. A null id mints a fresh one (a create); a given id
    // is honored (a restore, so listener ids survive restarts).
    private async Task<Listener> BindAsync(ListenerConfig config, ListenerId? id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            throw new ArgumentException("Listener name is required.", nameof(config));
        if (string.IsNullOrWhiteSpace(config.BindAddress))
            throw new ArgumentException("Listener bind address is required.", nameof(config));

        // A malformed bind address is an operator mistake (400, not 409): the
        // parse shapes are shared with the startup path, rethrown as
        // ArgumentException to say so. The port reservation that follows
        // stays an InvalidOperationException -- a bind refused for an address
        // in use is a conflict (409), not a malformed request.
        try
        {
            ValidateBindShape(config);
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message, nameof(config));
        }

        ReserveBind(config);

        // The same non-loopback plain-HTTP warning the startup path logs: a
        // deliberate posture choice, named rather than silent.
        if (config.Transport == ListenerTransport.Http)
        {
            var (host, _) = TransportHost.ParseBindAddress(config.BindAddress);
            if (!IPAddress.IsLoopback(host))
            {
                _logger.LogWarning(
                    "Runtime listener '{ListenerName}' binds plain HTTP on non-loopback {BindAddress}: "
                    + "the beacon identifies implants by their handshake id alone and the operator API "
                    + "rides the same socket in the clear. Keep plain HTTP on loopback unless a "
                    + "TLS-terminating edge fronts this host (architecture.md Sec 8).",
                    config.Name, config.BindAddress);
            }
        }

        return config.Transport switch
        {
            ListenerTransport.Http or ListenerTransport.Https
                or ListenerTransport.Mtls or ListenerTransport.HttpsEnvelope
                => await CreateHttpListenerAsync(config, id, cancellationToken).ConfigureAwait(false),
            ListenerTransport.Dns or ListenerTransport.Smb or ListenerTransport.Tcp
                => await CreateStreamListenerAsync(config, id, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException(
                $"Transport {config.Transport} is not supported for runtime listeners.", nameof(config)),
        };
    }

    /// <summary>
    /// Removes a runtime-created listener: unbinds its socket (stopping its
    /// stream service, or withdrawing its Kestrel endpoint -- Kestrel drains
    /// the socket for up to its shutdown timeout first), takes it out of the
    /// registry, and deletes its persisted definition so a restart does not
    /// resurrect it. Returns false when the listener is not runtime-managed.
    /// </summary>
    public async Task<bool> RemoveAsync(ListenerId listener, CancellationToken cancellationToken = default)
    {
        if (!_runtime.TryRemove(listener, out var entry))
            return false;

        if (entry.Service is not null)
        {
            await entry.Service.StopAsync(cancellationToken).ConfigureAwait(false);
            if (entry.Service is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (entry.Service is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        else
        {
            // The Kestrel reloader drains and unbinds the endpoint on its own
            // schedule (seconds); the registry entry goes now so the roster
            // stops reporting it immediately.
            _endpoints.WithdrawEndpoint(EndpointKey(listener));
        }

        await _listeners.RemoveAsync(listener, cancellationToken).ConfigureAwait(false);
        await _definitions.RemoveAsync(listener.Value, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Runtime listener {ListenerId} ({Name}) removed.", listener, entry.Listener.Name);
        return true;
    }

    private async Task<Listener> CreateHttpListenerAsync(
        ListenerConfig config, ListenerId? id, CancellationToken cancellationToken)
    {
        var (host, port) = TransportHost.ParseBindAddress(config.BindAddress);

        // The TLS-shaped transports publish an https URL; the HTTPS defaults
        // the host registered carry the CA-backed termination, and the
        // endpoint's own configuration entry names the certificate mode the
        // transport needs: the mTLS-shaped listeners request the client
        // certificate (enrollment still rides the same socket certificate-less,
        // so the request is optional at TLS and demanded at the application
        // layer), while the https transport publishes none -- the Kestrel
        // default never asks, the fingerprint rule the https posture is built
        // on (architecture.md Sec 8/9).
        var scheme = config.Transport == ListenerTransport.Http ? "http" : "https";
        var clientCertificateMode = config.Transport is ListenerTransport.Mtls or ListenerTransport.HttpsEnvelope
            ? nameof(ClientCertificateMode.AllowCertificate)
            : null;
        var listener = Listener.Define(
            id ?? ListenerId.New(), config.Name, config.Transport, config.BindAddress, config.PublicEndpoint,
            _clock.GetUtcNow(), config.EngagementId);

        _endpoints.PublishEndpoint(EndpointKey(listener.Id), $"{scheme}://{config.BindAddress}", clientCertificateMode);

        // The reloader binds asynchronously and reports failures only to the
        // log, so observe the outcome: the port must start accepting within
        // the probe window, else roll the entry back and refuse the create.
        if (!await WaitForAcceptingAsync(host, port, cancellationToken).ConfigureAwait(false))
        {
            _endpoints.WithdrawEndpoint(EndpointKey(listener.Id));
            throw new InvalidOperationException(
                $"Listener '{config.Name}' could not bind {config.BindAddress}: the socket never opened "
                + "(the address is likely in use); see the teamserver log.");
        }

        await _listeners.RegisterAsync(listener, cancellationToken).ConfigureAwait(false);
        _runtime[listener.Id] = new RuntimeEntry(listener, null);
        _logger.LogInformation(
            "Runtime listener {Name} ({Transport}) bound on {BindAddress} for {PublicEndpoint}.",
            config.Name, config.Transport, config.BindAddress, config.PublicEndpoint);
        return listener;
    }

    private async Task<Listener> CreateStreamListenerAsync(
        ListenerConfig config, ListenerId? id, CancellationToken cancellationToken)
    {
        var listener = Listener.Define(
            id ?? ListenerId.New(), config.Name, config.Transport, config.BindAddress, config.PublicEndpoint,
            _clock.GetUtcNow(), config.EngagementId);

        // The same per-entry services the startup path registers, started on
        // demand: each binds its socket and registers the aggregate itself --
        // bind-then-register, the shape every transport follows.
        IHostedService service = config.Transport switch
        {
            ListenerTransport.Dns => new DnsListenerService(
                listener,
                _services.GetRequiredService<DnsBeaconBridge>(),
                _listeners,
                _services.GetRequiredService<ILoggerFactory>().CreateLogger<DnsListenerService>()),
            ListenerTransport.Smb => new SmbListenerService(
                listener,
                _services.GetRequiredService<StreamBeaconBridge>(),
                _listeners,
                _services.GetRequiredService<ILoggerFactory>().CreateLogger<SmbListenerService>()),
            ListenerTransport.Tcp => new TcpListenerService(
                listener,
                _services.GetRequiredService<StreamBeaconBridge>(),
                _listeners,
                _services.GetRequiredService<ILoggerFactory>().CreateLogger<TcpListenerService>()),
            _ => throw new ArgumentException(
                $"Transport {config.Transport} is not a stream transport.", nameof(config)),
        };

        try
        {
            await service.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Runtime listener '{Name}' failed to bind {BindAddress}.", config.Name, config.BindAddress);
            throw new InvalidOperationException(
                $"Listener '{config.Name}' could not bind {config.BindAddress}: {ex.Message}");
        }

        // The service registers itself into the registry once its socket is
        // bound; the create answer waits for that registration so "running"
        // means listening, not "scheduled to listen".
        var registrationDeadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(2);
        while (await _listeners.FindAsync(listener.Id, cancellationToken).ConfigureAwait(false) is null)
        {
            if (_clock.GetUtcNow() > registrationDeadline)
            {
                await StopQuietlyAsync(service).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Listener '{config.Name}' could not bind {config.BindAddress}: the service never registered its socket.");
            }
            await Task.Delay(BindProbeInterval, cancellationToken).ConfigureAwait(false);
        }

        _runtime[listener.Id] = new RuntimeEntry(listener, service);
        _logger.LogInformation(
            "Runtime listener {Name} ({Transport}) bound on {BindAddress} for {PublicEndpoint}.",
            config.Name, config.Transport, config.BindAddress, config.PublicEndpoint);
        return listener;
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

    // The endpoint key under Kestrel:Endpoints. The listener id, not the name:
    // names can repeat or carry characters configuration keys would rather not.
    private static string EndpointKey(ListenerId listener) => $"rod-{listener.Value:N}";

    // The persisted shape of a bound, engagement-scoped listener.
    private static ListenerDefinition DefinitionOf(Listener listener)
        => new(
            listener.Id.Value,
            listener.EngagementId!.Value,
            listener.Name,
            listener.Transport.WireName(),
            listener.BindAddress,
            listener.PublicEndpoint,
            listener.CreatedAt,
            listener.RepointedAt);

    // Pre-create validation for the bind address shape, per transport. The
    // host:port shapes parse exactly as the startup path parses them.
    private static void ValidateBindShape(ListenerConfig config)
    {
        if (config.Transport == ListenerTransport.Smb)
        {
            // A bare pipe name; the pipe server creates it on bind.
            if (config.BindAddress.Contains(':', StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"SMB bind address '{config.BindAddress}' is a bare pipe name, not host:port.");
            return;
        }

        _ = TransportHost.ParseBindAddress(config.BindAddress);
    }

    // Reserves the TCP-shaped bind's port exclusively for a moment, so a bind
    // that would collide with a live socket (another listener, or anything
    // else on the host) is refused up front -- the Kestrel reloader reports
    // its bind failures only to the log, and nothing downstream could tell
    // the create from a silent no-op. The moment between release and the real
    // bind is the usual check-then-act window; operator tooling, not a lock,
    // is the right answer at this layer.
    private static void ReserveBind(ListenerConfig config)
    {
        if (config.Transport == ListenerTransport.Smb)
            return;

        var (host, port) = TransportHost.ParseBindAddress(config.BindAddress);

        if (config.Transport == ListenerTransport.Dns)
        {
            using var udp = new UdpClient(new IPEndPoint(host, port));
            return;
        }

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

    // Polls until the address accepts a TCP connection, the observable form of
    // "the reloader bound it". A wildcard bind is probed on loopback -- the
    // socket any wildcard bind necessarily covers.
    private async Task<bool> WaitForAcceptingAsync(IPAddress host, int port, CancellationToken cancellationToken)
    {
        var probeHost = host.Equals(IPAddress.Any) || host.Equals(IPAddress.IPv6Any) ? IPAddress.Loopback : host;
        var deadline = _clock.GetUtcNow() + BindProbeTimeout;
        while (_clock.GetUtcNow() < deadline)
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
}
