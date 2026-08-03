using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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

        // Audit port -> walking-skeleton in-memory adapter (roadmap M1.4). The
        // hash-chained store replaces this in place at M2.3; the port shape is
        // stable for it.
        services.AddSingleton<IAuditStore, InMemoryAuditStore>();

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
    /// </remarks>
    public static IWebHostBuilder UseRodMtls(this IWebHostBuilder builder, int httpsPort)
    {
        builder.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(httpsPort, listen =>
            {
                listen.UseHttps(https =>
                {
                    // The dev CA doubles as the server identity in the skeleton;
                    // a real deployment presents a proper server certificate. The
                    // implant client trusts the CA (see test client validation).
                    https.ServerCertificateSelector = (_, _) =>
                        kestrel.ApplicationServices.GetRequiredService<IImplantCertificateAuthority>().GetCaCertificate();
                    https.ClientCertificateMode =
                        Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
                    https.ClientCertificateValidation = (cert, chain, errors) =>
                        ClientCertificateChainsToCa(cert, chain, kestrel.ApplicationServices);
                    https.CheckCertificateRevocation = false;
                });
            });
        });
        return builder;
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
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate,
        System.Security.Cryptography.X509Certificates.X509Chain? chain,
        IServiceProvider services)
    {
        if (certificate is null || chain is null)
            return false;

        var ca = services.GetRequiredService<IImplantCertificateAuthority>().GetCaCertificate();
        chain.ChainPolicy.RevocationMode =
            System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags =
            System.Security.Cryptography.X509Certificates.X509VerificationFlags.AllowUnknownCertificateAuthority;
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
