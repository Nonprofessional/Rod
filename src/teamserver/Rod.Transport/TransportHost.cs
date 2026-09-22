using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Pki;
using Rod.CoreState.Sessions;
using Rod.CoreState.ShellSessions;
using Rod.CoreState.Staging;
using Rod.CoreState.Tasks;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;
using Rod.Transport.Listeners.Dns;
using Rod.Transport.Listeners.Providers;
using Rod.Transport.Listeners.Streams;
using Rod.Transport.Payloads;

namespace Rod.Transport;

/// <summary>
/// Assembles the teamserver HTTP host:
/// wires the core-state ports to their in-memory adapters, registers the
/// engagement and enrollment use cases, and maps the operator- and implant-facing
/// endpoints.
/// </summary>
public static class TransportHost
{
    /// <summary>Registers core-state ports, adapters, and use cases.</summary>
    public static IServiceCollection AddRodTransport(this IServiceCollection services)
        => services.AddRodTransport(configuration: null);

    /// <summary>
    /// Registers core-state ports, adapters, and use cases, and selects the audit
    /// and artifact store by configuration. When the
    /// <c>Audit:DataDirectory</c> section is present, the file-backed
    /// <see cref="FileAuditStore"/>/<see cref="FileArtifactStore"/> replace the
    /// in-memory adapters so the engagement trail and its artifacts survive a
    /// teamserver restart and infrastructure teardown; absent, the in-memory pair
    /// stays in place (the test host and any host that does not opt in are
    /// unchanged). The ports are stable either way -- callers stay agnostic.
    /// </summary>
    /// <param name="configuration">
    /// The application configuration. May be null, in which case the in-memory
    /// adapters are registered (identical to the parameterless overload).
    /// </param>
    public static IServiceCollection AddRodTransport(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration? configuration)
    {
        services.AddRouting();
        services.AddProblemDetails();

        // Core-state ports -> default in-memory adapters.
        services.AddSingleton<IOperatorRepository, InMemoryOperatorRepository>();
        // Operator password verifier -> default in-memory adapter. The
        // durable Postgres adapter replaces this from Rod.Persistence (ADR 0003)
        // the same way it replaces the repository above; the port is stable either
        // way. Operator authentication itself (cookie sessions, login) is wired in
        // Rod.Operators via AddRodOperatorAuth.
        services.AddSingleton<IOperatorCredentialStore, InMemoryOperatorCredentialStore>();
        // Operator API tokens (architecture.md Sec 9): the same default /
        // durable swap shape as the credential store above.
        services.AddSingleton<IOperatorApiTokenStore, InMemoryOperatorApiTokenStore>();
        services.AddSingleton<IEngagementRepository, InMemoryEngagementRepository>();
        services.AddSingleton<IStagerTokenService, InMemoryStagerTokenService>();
        services.AddSingleton<IImplantRepository, InMemoryImplantRepository>();
        // Implant CA (architecture.md Sec 9): the self-signed DevCertificateAuthority
        // is the default; an externally provisioned engagement CA,
        // supplied as PEM files via the Pki section, replaces it for production
        // (FileBackedCertificateAuthority). Mirrors the Audit:DataDirectory opt-in
        // below: presence selects the production adapter, absence keeps the dev
        // default and every existing test unchanged. The authority is constructed
        // eagerly so a missing, unreadable, or mismatched CA fails the host at
        // startup, not at the first enrollment.
        var pkiCertPath = configuration?["Pki:CaCertificatePath"];
        var pkiKeyPath = configuration?["Pki:CaPrivateKeyPath"];
        if (!string.IsNullOrWhiteSpace(pkiCertPath) || !string.IsNullOrWhiteSpace(pkiKeyPath))
        {
            if (string.IsNullOrWhiteSpace(pkiCertPath) || string.IsNullOrWhiteSpace(pkiKeyPath))
                throw new InvalidOperationException(
                    "Pki:CaCertificatePath and Pki:CaPrivateKeyPath must be configured together; supply both or neither.");
            services.AddSingleton<IImplantCertificateAuthority>(
                new FileBackedCertificateAuthority(new FileBackedCertificateAuthorityOptions(
                    pkiCertPath!, pkiKeyPath!, configuration?["Pki:CaPrivateKeyPassphrase"])));
        }
        else
        {
            services.AddSingleton<IImplantCertificateAuthority, DevCertificateAuthority>();
        }
        // The session registry wears its last-seen decorator (architecture.md
        // Sec 10.3): every open, touch, and sweep-close also advances the
        // implant row's durable LastSeenAt stamp, so an offline implant still
        // answers "when did we last hear from it". The persistence host
        // re-wraps its Postgres registry the same way when it swaps in.
        services.AddSingleton<ISessionRegistry>(sp => new LastSeenSessionRegistry(
            new InMemorySessionRegistry(),
            sp.GetRequiredService<IImplantRepository>()));
        // Shell sessions (architecture.md Sec 8, the shellcatch transport):
        // the caught reverse-shell registry, the anonymous-arrival sibling
        // of the implant session registry above; the in-memory default
        // pairs with a Postgres adapter when the durable host swaps in.
        services.AddSingleton<IShellSessionRegistry, InMemoryShellSessionRegistry>();
        // Web-shell endpoints (architecture.md Sec 5.2's Web-shell class):
        // the register use case and its profile store, anchored on
        // WebShell-class implant rows. Execution rides the normal task
        // lifecycle from the web-shell routes; the adapter round trips get
        // their own named client so the budget and redirects stay
        // endpoint-shaped, not operator-API-shaped.
        services.AddSingleton<CoreState.WebShells.IWebShellProfileRepository, CoreState.WebShells.InMemoryWebShellProfileRepository>();
        services.AddSingleton<CoreState.Application.WebShellService>();
        services.AddHttpClient("webshells");
        // The shellcatch hub (architecture.md Sec 8): the rendezvous between
        // the operator shell routes and the listener service's held
        // connections, keyed by shell session id -- the shell-facing analog
        // of the live channel hub below.
        services.AddSingleton<Listeners.ShellCatch.ShellCatchHub>();
        services.AddSingleton<ITaskRepository, InMemoryTaskRepository>();
        // Task-queue wake (architecture.md Sec 10.3): TaskService releases it
        // on every accepted enqueue and the beacon writer parks on it, so a
        // queued task is pushed downstream immediately and an idle fleet
        // costs nothing -- no poll loop in the writer path.
        services.AddSingleton<ITaskDispatchWake, InMemoryTaskDispatchWake>();
        // Live channel hub (architecture.md Sec 10.3, the streaming task
        // shape): the rendezvous between the operator input route and the
        // beacon stream's dispatch writer, keyed by implant. A stream
        // attaches its sink on handshake and detaches on stream end.
        services.AddSingleton<Channels.LiveChannelHub>();
        // Task relay hub (architecture.md Sec 10.1 tunnel, Sec 10.3): the
        // operator-side relay binds -- a teamserver-bound TCP listener
        // bridged onto a live tunnel channel, so unmodified tooling rides the
        // tunnel without per-byte input posts. Fed by the beacon ingest's
        // channel output path and the live channel hub's input path.
        services.AddSingleton<Channels.TaskRelayHub>();
        // SOCKS proxy hub (architecture.md Sec 10.1 tunnel, Sec 14): the
        // multiplexed relay -- a SOCKS5 listener bridged onto a tunnel.socks
        // channel, every connection under its id on the one task.
        services.AddSingleton<Channels.SocksProxyHub>();

        // Listener registry: the bound C2 ingress the teamserver is
        // terminating. Populated at startup by UseRodListeners and by runtime
        // creates; read-only from the operator API. The registry is the live
        // view -- the engagement association and its durability live in the
        // store below.
        services.AddSingleton<IListenerRegistry, InMemoryListenerRegistry>();
        // The durable home for engagement-scoped listener definitions:
        // in-memory by default (the process's lifetime, like the rest of core
        // state without Postgres), Postgres-backed when the connection string
        // is set (the composition root swaps the adapter).
        services.AddSingleton<Rod.CoreState.Listeners.IListenerStore, Rod.CoreState.Listeners.InMemoryListenerStore>();
        // The rendered launcher rows: the operator's re-copy/revoke list for
        // the one-liner delivery surface, in-memory by default and
        // Postgres-backed when the connection string is set.
        services.AddSingleton<Rod.CoreState.Launchers.ILauncherStore, Rod.CoreState.Launchers.InMemoryLauncherStore>();
        // Runtime listener management: create/remove listeners while the host
        // serves. The Kestrel half activates only on a host that binds real
        // listeners (UseRodListeners); the stream half works on any host.
        services.AddSingleton<ListenerManager>();
        // The startup restore pass: rebind what the store holds so a restart
        // gives the engagements back their ingress.
        services.AddHostedService<ListenerRestoreService>();
        // The stream-contact bridges (DNS and named-pipe/raw-TCP) are shared
        // singletons: the startup hosted services and the runtime listener
        // manager both resolve them, so they register here unconditionally --
        // a host with no stream listeners configured still serves runtime
        // creates for those transports.
        services.AddSingleton<DnsBeaconBridge>();
        services.AddSingleton<StreamBeaconBridge>();

        // Audit, artifact, and payload stores: in-memory by default -- the
        // hash-chained trail and first-class evidence objects -- or file-backed
        // when the Audit:DataDirectory section is configured -- the trail,
        // artifacts, and built payloads survive a teamserver restart and
        // infrastructure teardown, the acceptance point. The ports are stable
        // either way; only the adapter is swapped. The durable trio is the
        // Postgres stand-in by default., behind the same
        // ports.
        var dataDirectory = configuration?["Audit:DataDirectory"];
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            var persistence = new AuditPersistenceOptions { DataDirectory = dataDirectory };
            services.AddSingleton(persistence);
            services.AddSingleton<IAuditStore, FileAuditStore>();
            services.AddSingleton<IArtifactStore, FileArtifactStore>();
            services.AddSingleton<IPayloadStore, FilePayloadStore>();
        }
        else
        {
            // Audit port -> default in-memory adapter. The store is
            // hash-chained per engagement: tampering with a stored
            // event breaks the chain at the next link.
            services.AddSingleton<IAuditStore, InMemoryAuditStore>();

            // Artifact store port -> default in-memory adapter. First-class evidence objects attached to tasks; consumed by
            // the operator layer and beacon ingest later.
            services.AddSingleton<IArtifactStore, InMemoryArtifactStore>();

            // Payload store port -> default in-memory adapter. Built
            // payloads await retrieval; the file-backed adapter replaces it when
            // Audit:DataDirectory is set.
            services.AddSingleton<IPayloadStore, InMemoryPayloadStore>();
        }

        // Live-event bus port -> a no-op default. Transport must not reference
        // the operator layer (architecture test LayerDependencyTests), so the
        // real, channel-backed implementation lives in Rod.Operators and the
        // composition root replaces this registration via AddRodOperators. The
        // no-op keeps the core transport host self-sufficient and its unit tests
        // operator-free.
        services.AddSingleton<ILiveEventBus, NullLiveEventBus>();

        // Task-verb gate -> the class-table default. The tradecraft layer
        // replaces this with the registry-backed resolver via AddRodTradecraft
        // (the same replace-the-default shape as the bus above). Registering
        // the default also lets the container construct TaskService through
        // its fullest constructor -- the one that carries the dispatch wake --
        // in hosts that never opt into the tradecraft layer.
        services.AddSingleton<ITaskCapabilityResolver, ClassTableCapabilityResolver>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<EngagementService>();
        services.AddSingleton<EnrollmentService>();
        services.AddSingleton<HandshakeService>();
        services.AddSingleton<TaskService>();
        services.AddSingleton<ImplantService>();
        // The beacon compositions every transport shares (architecture.md
        // Sec 8): the upstream frame ingest (results, exfil, staged pulls,
        // channel output) and the downstream tasking marshal (signed
        // TaskRequests, dispatch audit, staged chunk runs). Every live
        // beacon carriage and the plain-HTTP envelope contact route
        // through this pair, so a frame is captured and a task delivered
        // identically regardless of which transport carried it.
        services.AddSingleton<Endpoints.BeaconIngest>();
        services.AddSingleton<Endpoints.BeaconTasking>();
        // The per-artifact contact key bookkeeping (architecture.md Sec 8/9):
        // the enroll-time implant-to-key binding and the contact counter
        // floor. Singleton because the binding spans the enroll route and the
        // contact route, and the floor spans every contact an implant makes.
        services.AddSingleton<Endpoints.EnvelopeContactKeys>();
        // The plain-HTTP envelope contact handler (architecture.md Sec 8):
        // one POST is one poll contact, the same frames the live carriages
        // carry as delimited sequences in ordinary request/response bodies.
        services.AddSingleton<Endpoints.EnvelopeBeaconContact>();
        // The store-and-forward half of the degraded channel discipline
        // (architecture.md Sec 10.3): the parking queue operator input waits
        // in against a poll-carrier implant that opted in, drained into its
        // contact responses. Singleton like the live sink hub it mirrors.
        services.AddSingleton<Channels.DegradedChannelHub>();
        // The WebSocket beacon stream (architecture.md Sec 8, the web
        // posture's interactive tier): the web family's live session,
        // over a WebSocket on the plain-HTTP listener family, authenticated
        // and sealed the way the envelope contact is.
        services.AddSingleton<Endpoints.WebSocketBeaconStream>();

        // Session staleness sweep (architecture.md Sec 10.3): the live values
        // are always registered -- the settings endpoints read them and the
        // composition root seeds them from the Sessions:Staleness section
        // (defaults absent it, fail-loudly on a present-but-misconfigured
        // one). The hosted sweeper itself runs only when a configuration is
        // supplied -- the composition root always has one, the bare test host
        // never opts in and drives passes directly.
        var staleness = configuration is not null
            ? SessionStalenessOptions.FromConfiguration(configuration)
            : SessionStalenessOptions.Default;
        services.AddSingleton(new SessionRuntimeSettings(
            staleness,
            persistencePath: configuration?["RuntimeSettings:FilePath"]));
        if (configuration is not null)
        {
            services.AddSingleton(staleness);
            services.AddSingleton<SessionSweepService>();
            // Registered as a plain singleton plus the hosted wrapper, so the
            // concrete type stays resolvable (tests drive a pass directly).
            services.AddSingleton<SessionStalenessSweeper>();
            services.AddHostedService(sp => sp.GetRequiredService<SessionStalenessSweeper>());
        }

        // Build pipeline (architecture.md Sec 6, ADR 0009): the build-unit registry and
        // the orchestrator that drives it. The .NET slot holds the real in-tree
        // reference build unit (compiles the .NET reference implant via dotnet
        // publish); the stub unit is the contract reference and is exercised by its
        // own unit tests, not the live host. Community build units for other
        // languages (Go/C/Nim) live out-of-tree. The service is audit-agnostic by
        // design -- the payload-build endpoint in transport composes the
        // PayloadBuilt audit write, the same way the beacon stream composes the
        // task-completion write.
        var buildUnits = new InMemoryBuildUnitRegistry();
        // The Rust build unit (Sec 12.2: .NET is the control plane, Rust the
        // implant): its source tree follows the same explicit-config shape --
        // unset keeps the walk-up default, a configured-but-missing directory
        // fails startup loudly. An absent cargo on PATH does not fail startup
        // (the unit fails its builds loudly instead).
        var rustSourceDirectory = configuration?["Build:RustSourceDirectory"];
        if (!string.IsNullOrWhiteSpace(rustSourceDirectory)
            && !Directory.Exists(rustSourceDirectory))
            throw new InvalidOperationException(
                $"The configured rust source directory '{rustSourceDirectory}' does not exist.");
        buildUnits.Register(new Rod.BuildPipeline.PayloadBuild.RustBuildUnit(rustSourceDirectory));
        services.AddSingleton<IBuildUnitRegistry>(buildUnits);
        // The post-build transform chain (architecture.md Sec 6, the transform
        // seam): config-listed out-of-tree transforms under Build:Transforms,
        // the same explicit-list loading shape as Tradecraft:Modules. A
        // missing section is the empty chain -- no in-tree transform ships,
        // the empty chain is the seam. A bad entry fails startup loudly: an
        // operator must never believe wrapped bytes are stored when the raw
        // build output is.
        var transformEntries = configuration?.GetSection(PayloadTransformLoader.TransformsSectionKey)
            .Get<string[]?>() ?? Array.Empty<string?>();
        services.AddSingleton(new PayloadTransformChain(PayloadTransformLoader.Load(transformEntries)));
        services.AddSingleton<PayloadBuildService>();
        // Background payload builds: the same build pipeline behind a job queue,
        // so a toolchain build neither holds a request open nor dies with a
        // browser refresh. Registered as the hosted service too so the worker
        // starts and stops with the host.
        services.AddSingleton<PayloadBuildJobService>();
        services.AddHostedService(sp => sp.GetRequiredService<PayloadBuildJobService>());

        return services;
    }


    /// <summary>
    /// Binds one socket per configured listener (, architecture.md Sec 8)
    /// and registers each into the <see cref="IListenerRegistry"/>. Each entry
    /// names its transport by wire name and the provider registry resolves the
    /// bind: an HTTP-family entry opens a Kestrel socket under its TLS posture
    /// (the mTLS entry terminating mutual TLS on the configured implant CA),
    /// a socket-owning entry runs its hosted service. The bind address (what
    /// Kestrel opens) and the public endpoint (what implants dial -- typically
    /// a redirector) are independent, so a burned redirector is replaced
    /// without touching this.
    /// </summary>
    /// <remarks>
    /// The registry is resolved from the DI container at host start
    /// (<see cref="KestrelServerOptions.ApplicationServices"/>); <see cref="AddRodTransport"/>
    /// registers the in-memory adapter. Call after <c>AddRodTransport</c>.
    /// </remarks>
    public static IWebHostBuilder UseRodListeners(
        this IWebHostBuilder builder,
        IReadOnlyList<ListenerConfig> listeners,
        TimeProvider? clock = null)
    {
        // Every entry's transport resolves through the provider registry, the
        // same authority the runtime create answers to: an unknown name fails
        // the boot loudly (a configuration typo must not surface as a listener
        // that never binds), and the provider's canonical wire name is what
        // the listener record carries.
        var entries = listeners
            .Select(config => (Config: config, Provider: TransportProviders.Find(config.Transport)
                ?? throw new InvalidOperationException(
                    $"Unknown listener transport '{config.Transport}' in the startup Listeners section.")))
            .ToList();

        // The socket-owning transports do not ride Kestrel: they are hosted
        // services, one per entry, built by each provider's factory. Each
        // entry's service binds its socket and registers itself into the
        // listener registry, the same bind-then-register shape the Kestrel
        // path follows.
        var hostedEntries = entries.Where(e => e.Provider is HostedServiceTransportProvider).ToArray();
        if (hostedEntries.Length > 0)
        {
            builder.ConfigureServices(services =>
            {
                foreach (var (config, provider) in hostedEntries)
                {
                    var hosted = (HostedServiceTransportProvider)provider;
                    // A plain IHostedService registration, not AddHostedService:
                    // the factory's return type is the interface (each
                    // provider's own service type), and TryAddEnumerable keys
                    // on the implementation type it would never see.
                    services.AddSingleton<IHostedService>(sp => hosted.CreateService(
                        sp,
                        sp.GetRequiredService<IListenerRegistry>(),
                        Listener.Define(
                            ListenerId.New(), config.Name, provider.Transport,
                            config.BindAddress, config.PublicEndpoint,
                            sp.GetRequiredService<TimeProvider>().GetUtcNow())));
                }
            });
        }

        builder.ConfigureKestrel(kestrel =>
        {
            var registry = kestrel.ApplicationServices.GetRequiredService<IListenerRegistry>();
            var now = (clock ?? TimeProvider.System).GetUtcNow();

            foreach (var (config, provider) in entries)
            {
                // The socket-owning transports own their sockets in their
                // hosted services; Kestrel sees only the HTTP family.
                if (provider is not KestrelEndpointProvider kestrelProvider)
                    continue;

                var (host, port) = ParseBindAddress(config.BindAddress);

                // A plain-HTTP bind is the loopback dev posture: no TLS and no
                // client certificates, so the implant routes on it identify
                // implants by their handshake id alone and the operator API
                // rides the same socket in the clear (architecture.md Sec 8).
                // A non-loopback bind is a deliberate TLS-terminating-edge
                // deployment at best; name the tradeoff at startup so the
                // choice is visible, not silent.
                if (kestrelProvider.Posture.Scheme == "http" && !IPAddress.IsLoopback(host))
                {
                    kestrel.ApplicationServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Rod.Transport.TransportHost")
                        .LogWarning(
                            "Listener '{ListenerName}' binds plain HTTP on non-loopback {BindAddress}: "
                            + "the beacon identifies implants by their handshake id alone and the operator API "
                            + "rides the same socket in the clear. Keep plain HTTP on loopback unless a "
                            + "TLS-terminating edge fronts this host (architecture.md Sec 8).",
                            config.Name, config.BindAddress);
                }

                var listener = Listener.Define(
                    ListenerId.New(), config.Name, provider.Transport, config.BindAddress, config.PublicEndpoint, now);

                // Bind first; register only once the socket is configured. The
                // listener's State moves to Running inside RegisterAsync.
                kestrel.Listen(host, port, listen =>
                {
                    // The single-port https shapes never request a client
                    // certificate: a TLS CertificateRequest is itself a
                    // fingerprint (an ordinary website never asks the visitor
                    // for one), and contacts authenticate at the application
                    // layer under the per-artifact key the build baked. The
                    // handshake carries a certificate exchange only for the
                    // server identity -- indistinguishable from ordinary web
                    // traffic (architecture.md Sec 8/9). DoH rides the same
                    // posture: the DNS grammar identifies by id alone, and
                    // the TLS shape must not fingerprint the resolver front.
                    if (string.Equals(provider.Transport, "https", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(provider.Transport, "doh", StringComparison.OrdinalIgnoreCase))
                        ConfigureHttps(listen, kestrel, config.PublicEndpoint);
                });

                registry.RegisterAsync(listener, CancellationToken.None).GetAwaiter().GetResult();
            }

            // Runtime listener management (architecture.md Sec 8): the manager's
            // push-only endpoint configuration rides Kestrel's config reloader,
            // so endpoints published after startup bind and withdrawn ones
            // unbind without touching the code-bound listeners above. The HTTPS
            // defaults apply to exactly those later-bound TLS endpoints --
            // startup listeners configured their own termination above.
            var manager = kestrel.ApplicationServices.GetRequiredService<ListenerManager>();
            kestrel.Configure(manager.KestrelSection, reloadOnChange: true);
            kestrel.ConfigureHttpsDefaults(manager.ApplyDynamicHttpsDefaults);
        });
        return builder;
    }


    // The single-port https termination: the CA-issued server leaf presents
    // as the server identity and nothing else is negotiated -- no client
    // certificate is requested at all (the default mode), so the TLS
    // handshake looks like any ordinary website's. Enrollment answers on the
    // stager token and contacts authenticate under the baked per-artifact
    // key, both at the application layer (architecture.md Sec 8/9).
    private static void ConfigureHttps(ListenOptions listen, KestrelServerOptions kestrel, string publicEndpoint)
    {
        listen.UseHttps(https =>
        {
            // A CA-issued server leaf naming the front's public host, not
            // the CA root itself: clients that pin the CA still verify the
            // server name against the leaf's SAN.
            https.ServerCertificateSelector = (_, _) =>
                kestrel.ApplicationServices.GetRequiredService<IImplantCertificateAuthority>()
                    .GetServerCertificate(ServerLeaf.HostOf(publicEndpoint));
        });
    }

    // Parses a "host:port" bind address into the form Kestrel.Listen takes. Accepts
    // an IP (v4 or v6) or "*" / "+" (any IP) -- mirrors ListenAnyIP semantics --
    // and a port. Throws a clear error on anything else so a misconfigured listener
    // fails fast at startup rather than binding silently to the wrong place.
    // Internal: the runtime listener manager validates operator-supplied bind
    // addresses with the same rule.
    internal static (IPAddress Host, int Port) ParseBindAddress(string bindAddress)
    {
        var span = bindAddress.AsSpan();
        IPAddress host;
        int port;

        // Bracketed IPv6, e.g. "[::1]:443".
        if (span.Length > 0 && span[0] == '[')
        {
            var end = span.IndexOf(']');
            if (end < 0 || end + 2 > span.Length || span[end + 1] != ':')
                throw new InvalidOperationException(
                    $"Listener bind address '{bindAddress}' is not a valid '[host]:port'.");
            if (!IPAddress.TryParse(span[1..end], out var parsedHost))
                throw new InvalidOperationException(
                    $"Listener bind address '{bindAddress}' has an unparseable host.");
            host = parsedHost;
            if (!int.TryParse(span[(end + 2)..], out port))
                throw new InvalidOperationException(
                    $"Listener bind address '{bindAddress}' has an unparseable port.");
        }
        else
        {
            var colon = span.LastIndexOf(':');
            if (colon < 0)
                throw new InvalidOperationException(
                    $"Listener bind address '{bindAddress}' is not a valid 'host:port'.");

            var hostPart = span[..colon];
            if (hostPart.SequenceEqual("*".AsSpan()) || hostPart.SequenceEqual("+".AsSpan()))
                host = IPAddress.Any;
            else if (!IPAddress.TryParse(hostPart, out var parsedHost))
                throw new InvalidOperationException(
                    $"Listener bind address '{bindAddress}' has an unparseable host.");
            else
                host = parsedHost;

            if (!int.TryParse(span[(colon + 1)..], out port))
                throw new InvalidOperationException(
                    $"Listener bind address '{bindAddress}' has an unparseable port.");
        }

        if (port < 1 || port > 65535)
            throw new InvalidOperationException(
                $"Listener bind address '{bindAddress}' has an out-of-range port.");

        return (host, port);
    }

    /// <summary>Maps the operator- and implant-facing endpoints onto a built application.</summary>
    public static WebApplication MapRodEndpoints(this WebApplication app)
    {
        // A built application is itself an IEndpointRouteBuilder, so the
        // mapping list lives once, in the raw-pipeline overload below.
        MapRodEndpoints((IEndpointRouteBuilder)app);
        return app;
    }

    /// <summary>Maps the operator- and implant-facing endpoints onto a raw pipeline.</summary>
    public static void MapRodEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapEngagementEndpoints();
        endpoints.MapEnrollmentEndpoints();
        endpoints.MapImplantEndpoints();
        endpoints.MapListenerEndpoints();
        // The standalone one-liner launchers: the paste-ready stage-2 fetch
        // renders, shared with the shell console's upgrade flow.
        endpoints.MapLauncherEndpoints();
        // The engagement's caught reverse shells: the shellcatch surface's
        // roster and console routes.
        endpoints.MapShellSessionEndpoints();
        // The engagement's web-shell endpoints: the register/list routes and
        // the synchronous execution arc.
        endpoints.MapWebShellEndpoints();
        // The host's bindable interfaces: the read view behind the listener
        // form's bind dropdown.
        endpoints.MapNetworkEndpoints();
        endpoints.MapPresenceEndpoints();
        endpoints.MapTaskEndpoints();
        endpoints.MapPayloadEndpoints();
        // Operator-facing runtime settings (the live session-presence knobs).
        endpoints.MapSettingsEndpoints();
        // Background payload builds: the job-queued face of the same pipeline.
        endpoints.MapPayloadJobEndpoints();
        // The per-engagement operational event log: the durable,
        // hash-chained audit trail read view. Distinct from the operators-layer
        // live SSE route (the transient fan-out).
        endpoints.MapAuditEndpoints();
        // First-class evidence objects linked to tasks: attach,
        // list, and retrieve artifacts per task, scoped by engagement.
        endpoints.MapArtifactEndpoints();
        // The built-in consumers of the event + task + artifact store: export the engagement timeline and report (JSON + Markdown),
        // reproducibility-stamped. Read-only projections of the evidence trail.
        endpoints.MapReportEndpoints();
        // The engagement close-out (architecture.md Sec 2 step 10): freeze,
        // export the evidence package, retire -- the path a finished
        // engagement takes out of service.
        endpoints.MapCloseoutEndpoints();
        // The plain-HTTP envelope contact (architecture.md Sec 8): the same
        // frames as delimited sequences in ordinary request/response bodies,
        // for implants with an HTTP client and a protobuf codec -- no
        // protocol stack beyond HTTP. Identity is the artifact key that
        // sealed the body, so any web front serves it.
        endpoints.MapEnvelopeBeaconEndpoints();
        // The WebSocket beacon stream: the web posture's live channel, the
        // same session every live carriage runs over the envelope's own auth
        // and frame grammar.
        endpoints.MapWebSocketBeaconEndpoints();
        // DNS-over-HTTPS: the DNS grammar's second carriage, served by the
        // doh listener that owns the arriving port.
        endpoints.MapDnsOverHttpsEndpoints();
        // A trivial health probe so the listener is observably up.
        endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    }

    /// <summary>
    /// Builds a ready-to-run <see cref="WebApplication"/> for <c>dotnet run</c>.
    /// </summary>
    public static WebApplication BuildApplication(string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
        builder.Services.AddRodTransport();

        return builder.Build().MapRodEndpoints();
    }

    /// <summary>
    /// A minimal <see cref="Microsoft.Extensions.Hosting.IHostBuilder"/> for tests.
    /// Callers apply <c>UseTestServer</c> (from <c>Microsoft.AspNetCore.TestHost</c>,
    /// an extension on <see cref="Microsoft.Extensions.Hosting.IHostBuilder"/>),
    /// <c>Build()</c> the host, and <c>GetTestClient()</c> for an in-memory
    /// <see cref="HttpClient"/>. Services and endpoints are wired the same way as
    /// <see cref="BuildApplication"/>.
    ///
    /// The optional <paramref name="configureServices"/> and
    /// <paramref name="mapEndpoints"/> hooks let a caller layer in additional
    /// services and endpoints after the transport core -- the operator layer
    /// (Rod.Operators) registers itself through them, since transport cannot
    /// reference that assembly (architecture test LayerDependencyTests). They
    /// default to no-op so existing callers are unaffected.
    /// </summary>
    /// <param name="configuration">
    /// Optional configuration forwarded to <see cref="AddRodTransport(IServiceCollection, IConfiguration?)"/>
    /// so a test host can select the durable audit/artifact stores via the
    /// <c>Audit:DataDirectory</c> section. Null keeps the in-memory
    /// adapters, matching every existing caller.
    /// </param>
    public static IHostBuilder CreateHostBuilder(
        string[]? args = null,
        Action<IServiceCollection>? configureServices = null,
        Action<IEndpointRouteBuilder>? mapEndpoints = null,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    => Host.CreateDefaultBuilder(args ?? Array.Empty<string>())
        .ConfigureWebHostDefaults(web => web
            .ConfigureServices(services =>
            {
                services.AddRodTransport(configuration);
                // Core authentication/authorization plumbing (no scheme, no
                // policy) so the middleware below is always safe to run, whether
                // or not a caller layers the operator cookie scheme on top via
                // AddRodOperatorAuth. Endpoints opt into the session with
                // RequireAuthorization; with no scheme configured such a request
                // simply fails closed rather than throwing at startup.
                services.AddAuthentication();
                services.AddAuthorization();
                configureServices?.Invoke(services);
            })
            .Configure(app => app
                .UseRouting()
                .UseRodTransportCore()
                .UseEndpoints(endpoints =>
                {
                    MapRodEndpoints(endpoints);
                    mapEndpoints?.Invoke(endpoints);
                })));

    /// <summary>
    /// The transport-critical middleware core every Rod host runs, in order:
    /// WebSocket upgrade support, then authentication and authorization ahead
    /// of the mapped endpoints. Shared between the composition root's
    /// <c>WebApplication</c> and <see cref="CreateHostBuilder"/>'s test hosts
    /// so the two pipelines cannot drift -- the WebSockets line is
    /// load-bearing (the beacon route's <c>AcceptWebSocketAsync</c> only works
    /// on a real Kestrel bind when the middleware runs, and a pipeline that
    /// loses it answers the beacon's upgrade with a 400; the TestServer
    /// harness upgrades without it, which is exactly why the gap once hid).
    /// Call after routing and before endpoint mapping/execution.
    /// </summary>
    public static IApplicationBuilder UseRodTransportCore(this IApplicationBuilder app)
        => app
            .UseWebSockets()
            .UseAuthentication()
            .UseAuthorization();
}
