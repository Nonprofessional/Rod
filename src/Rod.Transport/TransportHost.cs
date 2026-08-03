using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Pki;
using Rod.CoreState.Sessions;
using Rod.CoreState.Staging;
using Rod.CoreState.Tasks;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;

namespace Rod.Transport;

/// <summary>
/// Assembles the teamserver HTTP host for the walking skeleton (roadmap M1):
/// wires the core-state ports to their in-memory adapters, registers the
/// engagement and enrollment use cases, and maps the operator- and implant-facing
/// endpoints.
/// </summary>
public static class TransportHost
{
    /// <summary>Registers core-state ports, adapters, and use cases.</summary>
    public static IServiceCollection AddRodTransport(this IServiceCollection services)
    {
        services.AddRouting();
        services.AddProblemDetails();
        // gRPC server: the beacon stream terminates here (roadmap M1.3).
        services.AddGrpc();

        // Core-state ports -> walking-skeleton in-memory adapters (roadmap M1).
        services.AddSingleton<IOperatorRepository, InMemoryOperatorRepository>();
        services.AddSingleton<IEngagementRepository, InMemoryEngagementRepository>();
        services.AddSingleton<IStagerTokenService, InMemoryStagerTokenService>();
        services.AddSingleton<IImplantRepository, InMemoryImplantRepository>();
        services.AddSingleton<IImplantCertificateAuthority, DevCertificateAuthority>();
        services.AddSingleton<ISessionRegistry, InMemorySessionRegistry>();
        services.AddSingleton<ITaskRepository, InMemoryTaskRepository>();

        // Listener registry (roadmap M2.2): the bound C2 ingress the teamserver is
        // terminating. Populated at startup by UseRodListeners; read-only from the
        // operator API. Listeners are global infrastructure, not engagement-scoped.
        services.AddSingleton<IListenerRegistry, InMemoryListenerRegistry>();

        // Audit port -> walking-skeleton in-memory adapter. The store is
        // hash-chained per engagement (roadmap M2.3): tampering with a stored
        // event breaks the chain at the next link.
        services.AddSingleton<IAuditStore, InMemoryAuditStore>();

        // Artifact store port -> walking-skeleton in-memory adapter (roadmap
        // M2.3). First-class evidence objects attached to tasks; consumed by the
        // operator layer (M2.4) and beacon ingest later.
        services.AddSingleton<IArtifactStore, InMemoryArtifactStore>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<EngagementService>();
        services.AddSingleton<EnrollmentService>();
        services.AddSingleton<HandshakeService>();
        services.AddSingleton<TaskService>();

        return services;
    }

    /// <summary>
    /// Configures Kestrel to terminate mTLS using the configured implant CA
    /// (architecture.md Sec 9): the server presents a TLS certificate (the dev
    /// CA's own cert in the walking skeleton) and requires a client certificate
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
    /// Binds one socket per configured listener (roadmap M2.2, architecture.md Sec 8)
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
        builder.ConfigureKestrel(kestrel =>
        {
            var registry = kestrel.ApplicationServices.GetRequiredService<IListenerRegistry>();
            var now = (clock ?? TimeProvider.System).GetUtcNow();

            foreach (var config in listeners)
            {
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
            // The dev CA doubles as the server identity in the skeleton; a real
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
        // The implant-initiated beacon stream (roadmap M1.3): gRPC over the
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
    /// </summary>
    public static IHostBuilder CreateHostBuilder(string[]? args = null)
        => Host.CreateDefaultBuilder(args ?? Array.Empty<string>())
            .ConfigureWebHostDefaults(web => web
                .ConfigureServices(services => services.AddRodTransport())
                .Configure(app => app
                    .UseRouting()
                    .UseEndpoints(MapRodEndpoints)));
}
