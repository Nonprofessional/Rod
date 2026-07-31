using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Pki;
using Rod.CoreState.Staging;
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

        // Core-state ports -> walking-skeleton in-memory adapters (roadmap M1).
        services.AddSingleton<IOperatorRepository, InMemoryOperatorRepository>();
        services.AddSingleton<IEngagementRepository, InMemoryEngagementRepository>();
        services.AddSingleton<IStagerTokenService, InMemoryStagerTokenService>();
        services.AddSingleton<IImplantRepository, InMemoryImplantRepository>();
        services.AddSingleton<IImplantCertificateAuthority, DevCertificateAuthority>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<EngagementService>();
        services.AddSingleton<EnrollmentService>();

        return services;
    }

    /// <summary>Maps the operator- and implant-facing endpoints onto a built application.</summary>
    public static WebApplication MapRodEndpoints(this WebApplication app)
    {
        app.MapEngagementEndpoints();
        app.MapEnrollmentEndpoints();
        // A trivial health probe so the listener is observably up.
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        return app;
    }

    /// <summary>Maps the operator- and implant-facing endpoints onto a raw pipeline.</summary>
    public static void MapRodEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapEngagementEndpoints();
        endpoints.MapEnrollmentEndpoints();
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
