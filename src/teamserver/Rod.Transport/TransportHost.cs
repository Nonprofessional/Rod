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
using Rod.CoreState.Staging;
using Rod.CoreState.Tasks;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;
using Rod.Transport.Listeners.Dns;

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
        // gRPC server: the beacon stream terminates here. The
        // message caps enforce the rod.proto sizing contract (a single frame
        // stays well under 1 MiB; bulk data is chunked): 2 MiB leaves headroom
        // for the envelope and protobuf overhead above the 1 MiB payload budget,
        // and bounds every TaskResult/ExfilChunk an implant can send in one
        // frame.
        services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = 2 * 1024 * 1024;
            options.MaxSendMessageSize = 2 * 1024 * 1024;
        });

        // Core-state ports -> default in-memory adapters.
        services.AddSingleton<IOperatorRepository, InMemoryOperatorRepository>();
        // Operator password verifier -> default in-memory adapter. The
        // durable Postgres adapter replaces this from Rod.Persistence (ADR 0003)
        // the same way it replaces the repository above; the port is stable either
        // way. Operator authentication itself (cookie sessions, login) is wired in
        // Rod.Operators via AddRodOperatorAuth.
        services.AddSingleton<IOperatorCredentialStore, InMemoryOperatorCredentialStore>();
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
        services.AddSingleton<ISessionRegistry, InMemorySessionRegistry>();
        services.AddSingleton<ITaskRepository, InMemoryTaskRepository>();
        // Task-queue wake (architecture.md Sec 10.3): TaskService releases it
        // on every accepted enqueue and the beacon writer parks on it, so a
        // queued task is pushed downstream immediately and an idle fleet
        // costs nothing -- no poll loop in the writer path.
        services.AddSingleton<ITaskDispatchWake, InMemoryTaskDispatchWake>();

        // Listener registry: the bound C2 ingress the teamserver is
        // terminating. Populated at startup by UseRodListeners; read-only from the
        // operator API. Listeners are global infrastructure, not engagement-scoped.
        services.AddSingleton<IListenerRegistry, InMemoryListenerRegistry>();

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

        // Session staleness sweep (architecture.md Sec 10.3): registered only
        // when a configuration is supplied -- the composition root always has
        // one, the bare test host never opts in. The hosted sweeper runs the
        // threshold check on a timer; absent the Sessions:Staleness section it
        // uses the 15-minute default. A present-but-misconfigured section fails
        // startup loudly rather than leaving dead sessions on the roster.
        if (configuration is not null)
        {
            var staleness = SessionStalenessOptions.FromConfiguration(configuration);
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
        buildUnits.Register(new DotNetBuildUnit());
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

        return services;
    }

    /// <summary>
    /// Configures Kestrel to terminate mTLS using the configured implant CA
    /// (architecture.md Sec 9): the server presents a TLS certificate (the dev
    /// CA's own cert by default) and requires a client certificate
    /// that chains to the CA. Implant leaves are accepted; anything else is
    /// refused at the TLS layer, before any beacon handler runs.
    /// </summary>
    /// <remarks>
    /// Opt-in: existing TestServer-based tests and the operator API keep working
    /// over plain HTTP when this is not applied. A real deployment always applies
    /// it on the implant-facing endpoint. The CA is resolved from the DI container
    /// at connection time via <see cref="KestrelServerOptions.ApplicationServices"/>.
    /// Kept for the existing mTLS tests; <see cref="UseRodListeners"/> is the
    /// general path and an <see cref="ListenerTransport.Mtls"/> entry routes
    /// through the same <see cref="ConfigureMtlsHttps"/> helper.
    /// </remarks>
    public static IWebHostBuilder UseRodMtls(this IWebHostBuilder builder, int httpsPort)
    {
        builder.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(httpsPort, listen => ConfigureMtlsHttps(listen, kestrel));
        });
        return builder;
    }

    /// <summary>
    /// Binds one socket per configured listener (, architecture.md Sec 8)
    /// and registers each into the <see cref="IListenerRegistry"/>. Each entry picks
    /// its transport: <see cref="ListenerTransport.Http"/> opens a plain socket;
    /// <see cref="ListenerTransport.Mtls"/> opens an HTTPS socket that terminates
    /// mutual TLS using the configured implant CA. The bind address (what Kestrel
    /// opens) and the public endpoint (what implants dial -- typically a redirector)
    /// are independent, so a burned redirector is replaced without touching this.
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
        // DNS entries do not ride Kestrel: they are UDP datagram services,
        // registered as hosted services on the container the web host builds.
        // The bridge (sessions, tasking, audit composition) is one singleton
        // shared by every DNS entry; each entry's service binds its socket and
        // registers itself into the listener registry, the same
        // bind-then-register shape the Kestrel path follows.
        var dnsEntries = listeners.Where(l => l.Transport == ListenerTransport.Dns).ToArray();
        if (dnsEntries.Length > 0)
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<DnsBeaconBridge>();
                foreach (var entry in dnsEntries)
                    services.AddHostedService(sp => new DnsListenerService(
                        entry,
                        sp.GetRequiredService<DnsBeaconBridge>(),
                        sp.GetRequiredService<IListenerRegistry>(),
                        sp.GetRequiredService<TimeProvider>(),
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<DnsListenerService>()));
            });
        }

        builder.ConfigureKestrel(kestrel =>
        {
            var registry = kestrel.ApplicationServices.GetRequiredService<IListenerRegistry>();
            var now = (clock ?? TimeProvider.System).GetUtcNow();

            foreach (var config in listeners)
            {
                // The DNS listener owns its UDP socket in the hosted service;
                // Kestrel sees only the stream transports.
                if (config.Transport == ListenerTransport.Dns)
                    continue;

                var (host, port) = ParseBindAddress(config.BindAddress);
                var listener = Listener.Define(
                    ListenerId.New(), config.Name, config.Transport, config.BindAddress, config.PublicEndpoint, now);

                // Bind first; register only once the socket is configured. The
                // listener's State moves to Running inside RegisterAsync.
                kestrel.Listen(host, port, listen =>
                {
                    if (config.Transport == ListenerTransport.Mtls)
                        ConfigureMtlsHttps(listen, kestrel);
                });

                registry.RegisterAsync(listener, CancellationToken.None).GetAwaiter().GetResult();
            }
        });
        return builder;
    }

    // Applies the mTLS HTTPS configuration shared by UseRodMtls and the Mtls
    // listener: the dev CA presents as the server identity, a client certificate
    // is required, and it must chain to the CA. ApplicationServices resolves the
    // CA per connection.
    private static void ConfigureMtlsHttps(ListenOptions listen, KestrelServerOptions kestrel)
    {
        listen.UseHttps(https =>
        {
            // The dev CA doubles as the server identity by default; a real
            // deployment presents a proper server certificate. The implant client
            // trusts the CA (see test client validation).
            https.ServerCertificateSelector = (_, _) =>
                kestrel.ApplicationServices.GetRequiredService<IImplantCertificateAuthority>().GetCaCertificate();
            https.ClientCertificateMode =
                Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (cert, chain, errors) =>
                ClientCertificateChainsToCa(cert, chain, kestrel.ApplicationServices);
            https.CheckCertificateRevocation = false;
        });
    }

    // Parses a "host:port" bind address into the form Kestrel.Listen takes. Accepts
    // an IP (v4 or v6) or "*" / "+" (any IP) -- mirrors ListenAnyIP semantics --
    // and a port. Throws a clear error on anything else so a misconfigured listener
    // fails fast at startup rather than binding silently to the wrong place.
    private static (IPAddress Host, int Port) ParseBindAddress(string bindAddress)
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

    // True when the presented client cert chains to the configured implant CA.
    // Resolved per-connection from the DI container (ApplicationServices is
    // available by the time connections are accepted).
    //
    // AllowUnknownCertificateAuthority lets the chain resolve past our dev root
    // (which is not in a system trust store), but that flag alone would also
    // accept a self-signed cert -- its only error is UntrustedRoot, exactly what
    // the flag suppresses. So after building, we confirm the chain's root IS our
    // CA by thumbprint. A cert issued by any other root, or self-signed, is
    // refused here, before any beacon handler runs.
    private static bool ClientCertificateChainsToCa(
        X509Certificate2? certificate,
        X509Chain? chain,
        IServiceProvider services)
    {
        if (certificate is null || chain is null)
            return false;

        var ca = services.GetRequiredService<IImplantCertificateAuthority>().GetCaCertificate();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.ExtraStore.Add(ca);

        if (!chain.Build(certificate))
            return false;

        // The chain must terminate at our CA, not some other accepted root.
        return chain.ChainElements.Count > 0
            && chain.ChainElements[^1].Certificate.Thumbprint == ca.Thumbprint;
    }

    /// <summary>Maps the operator- and implant-facing endpoints onto a built application.</summary>
    public static WebApplication MapRodEndpoints(this WebApplication app)
    {
        app.MapEngagementEndpoints();
        app.MapEnrollmentEndpoints();
        app.MapImplantEndpoints();
        app.MapListenerEndpoints();
        app.MapPresenceEndpoints();
        app.MapTaskEndpoints();
        app.MapPayloadEndpoints();
        // The per-engagement operational event log: the durable,
        // hash-chained audit trail read view. Distinct from the operators-layer
        // live SSE route (the transient fan-out).
        app.MapAuditEndpoints();
        // First-class evidence objects linked to tasks: attach,
        // list, and retrieve artifacts per task, scoped by engagement.
        app.MapArtifactEndpoints();
        // The built-in consumers of the event + task + artifact store: export the engagement timeline and report (JSON + Markdown),
        // reproducibility-stamped. Read-only projections of the evidence trail.
        app.MapReportEndpoints();
        // The implant-initiated beacon stream: gRPC over the
        // mTLS-terminated HTTPS endpoint. Mapped alongside the operator API.
        app.MapGrpcService<BeaconEndpoint>();
        // A trivial health probe so the listener is observably up.
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        return app;
    }

    /// <summary>Maps the operator- and implant-facing endpoints onto a raw pipeline.</summary>
    public static void MapRodEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapEngagementEndpoints();
        endpoints.MapEnrollmentEndpoints();
        endpoints.MapImplantEndpoints();
        endpoints.MapListenerEndpoints();
        endpoints.MapPresenceEndpoints();
        endpoints.MapTaskEndpoints();
        endpoints.MapPayloadEndpoints();
        // The per-engagement operational event log: the durable,
        // hash-chained audit trail read view.
        endpoints.MapAuditEndpoints();
        // First-class evidence objects linked to tasks: attach,
        // list, and retrieve artifacts per task, scoped by engagement.
        endpoints.MapArtifactEndpoints();
        // The built-in consumers of the event + task + artifact store: export the engagement timeline and report (JSON + Markdown),
        // reproducibility-stamped. Read-only projections of the evidence trail.
        endpoints.MapReportEndpoints();
        // gRPC service binding is an IEndpointRouteBuilder extension; it works the
        // same on the raw pipeline (TestServer host) and the built application.
        endpoints.MapGrpcService<BeaconEndpoint>();
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
                .UseAuthentication()
                .UseAuthorization()
                .UseEndpoints(endpoints =>
                {
                    MapRodEndpoints(endpoints);
                    mapEndpoints?.Invoke(endpoints);
                })));
}
