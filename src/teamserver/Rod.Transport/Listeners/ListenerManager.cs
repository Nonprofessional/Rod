using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rod.CoreState.Engagements;
using Rod.CoreState.Listeners;
using Rod.CoreState.Pki;
using Rod.Transport.Listeners.Providers;

namespace Rod.Transport.Listeners;

/// <summary>
/// Runtime listener management: create and remove C2 ingress while the
/// teamserver serves, the operator-API counterpart to the startup
/// <c>Listeners</c> configuration (architecture.md Sec 8). The manager owns
/// the shared create/remove flow -- validation mapping, port reservation,
/// engagement scoping, persistence, the runtime roster -- and delegates each
/// transport's bind to its provider (<see cref="TransportProviders"/>, keyed
/// by wire name). Two provider shapes cover the in-tree six:
///
/// - <see cref="KestrelEndpointProvider"/>: the HTTP family
///   (<c>Http</c>, <c>Https</c>, <c>Mtls</c>) rides Kestrel's
///   endpoint-configuration reloader -- the provider publishes the bind
///   address as an endpoint URL into a push-only configuration source the
///   host registered with <c>KestrelServerOptions.Configure(..., reloadOnChange:
///   true)</c> and Kestrel binds (create) or drains and unbinds (remove) the
///   socket in response, under the transport's TLS posture. A create confirms
///   the bind by probing the port before reporting the listener as running --
///   the reloader's errors are logs, not exceptions, so the provider observes
///   the outcome instead of assuming it.
///
/// - <see cref="HostedServiceTransportProvider"/>: the socket-owning
///   transports (<c>Dns</c>, <c>Smb</c>, <c>Tcp</c>) run their sockets in
///   per-listener hosted services; the provider starts and stops one service
///   per listener on demand, the same services the startup path registers.
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
    /// the configuration (the provider's
    /// <see cref="Providers.ListenerTlsPosture">TLS posture</see>): the
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
        // The stored transport is the wire name a provider registered under,
        // case-insensitive. The retired https-envelope entry maps to its
        // nearest surviving transport, https -- the bind and the TLS
        // termination it always shared -- so a definition saved before the
        // retirement rebinds under its migrated shape instead of dying as
        // unknown.
        var transport = definition.Transport.Trim();
        if (transport.Replace("-", "").Equals("httpsenvelope", StringComparison.OrdinalIgnoreCase))
        {
            transport = "https";
            _logger.LogInformation(
                "Stored listener {ListenerId} ({Name}) carries the retired https-envelope transport; rebinding as https.",
                definition.Id, definition.Name);
        }
        else if (TransportProviders.Find(transport) is null)
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
        // The provider serving the transport's wire name owns everything the
        // bind needs beyond the shared reservation flow below: the shape
        // validation, the reservation, and the bind body itself. An unknown
        // name is a malformed request -- the registry is the authority for
        // what a runtime listener may name.
        var provider = TransportProviders.Find(config.Transport)
            ?? throw new ArgumentException(
                $"Transport '{config.Transport}' is not supported for runtime listeners.", nameof(config));
        try
        {
            provider.Validate(config);
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message, nameof(config));
        }

        provider.ReserveBind(config);

        // The same non-loopback plain-HTTP warning the startup path logs: a
        // deliberate posture choice, named rather than silent.
        if (string.Equals(config.Transport, "http", StringComparison.OrdinalIgnoreCase))
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

        var bound = await provider.BindAsync(
            new TransportBindContext(config, id, _services, _listeners, _endpoints, _clock, _logger),
            cancellationToken).ConfigureAwait(false);
        _runtime[bound.Listener.Id] = new RuntimeEntry(bound.Listener, bound.Service);
        return bound.Listener;
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
            _endpoints.WithdrawEndpoint(KestrelEndpointProvider.EndpointKey(listener));
        }

        await _listeners.RemoveAsync(listener, cancellationToken).ConfigureAwait(false);
        await _definitions.RemoveAsync(listener.Value, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Runtime listener {ListenerId} ({Name}) removed.", listener, entry.Listener.Name);
        return true;
    }

    // The persisted shape of a bound, engagement-scoped listener.
    private static ListenerDefinition DefinitionOf(Listener listener)
        => new(
            listener.Id.Value,
            listener.EngagementId!.Value,
            listener.Name,
            listener.Transport,
            listener.BindAddress,
            listener.PublicEndpoint,
            listener.CreatedAt,
            listener.RepointedAt);
}
