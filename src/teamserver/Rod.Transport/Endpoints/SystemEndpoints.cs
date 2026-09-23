using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState.Listeners;
using Rod.CoreState.Launchers;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing system page's read: the host's own facts and every
/// build unit's self-reported environment (architecture.md Sec 6/Sec 12.2).
/// The point is preflight honesty -- a deployment missing a cross-compiler
/// or the implant source tree learns it here, in one glance, instead of
/// inside a build job minutes after the operator asked for an artifact.
/// Read-only by construction: it probes (version queries, directory walks)
/// and composes nothing onto any store.
/// </summary>
public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/system").RequireAuthorization();
        group.MapGet("/", GetSystemAsync).WithName(nameof(GetSystemAsync));
        return endpoints;
    }

    private static IResult GetSystemAsync(
        IBuildUnitRegistry buildUnits,
        IServiceProvider services,
        IConfiguration configuration)
    {
        // Only units that can probe themselves report; an out-of-tree unit
        // without the capability is a silent row, not a fabricated one.
        var units = buildUnits.All
            .OfType<IBuildUnitEnvironment>()
            .Select(u => u.ReportEnvironment())
            .ToArray();
        return Results.Ok(new SystemInfoResponse(
            ServerSection.Of(),
            PersistenceSection.Of(services, configuration),
            units));
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record SystemInfoResponse(
        ServerSection Server,
        PersistenceSection Persistence,
        IReadOnlyList<BuildUnitEnvironmentReport> BuildUnits);

    /// <summary>
    /// Which adapter each store runs on, read off the composed instances --
    /// the in-memory/file/Postgres selection the composition root made at
    /// startup. The location names where the data lives: the configured
    /// data directory for the file-backed audit trio, or the Postgres
    /// target with its credentials masked -- never the connection string's
    /// secret half.
    /// </summary>
    public sealed record PersistenceSection(
        string DataDirectory,
        string? PostgresTarget,
        IReadOnlyList<StoreAdapter> Stores)
    {
        public static PersistenceSection Of(IServiceProvider services, IConfiguration configuration)
        {
            var stores = new List<StoreAdapter>
            {
                Store("audit trail", services.GetService<IAuditStore>()),
                Store("task artifacts", services.GetService<IArtifactStore>()),
                Store("payload library", services.GetService<IPayloadStore>()),
                Store("launchers", services.GetService<ILauncherStore>()),
                Store("listener definitions", services.GetService<IListenerStore>()),
            };
            return new PersistenceSection(
                configuration["Audit:DataDirectory"] ?? "",
                PostgresTargetOf(configuration["ConnectionStrings:Postgres"]),
                stores);
        }

        private static StoreAdapter Store(string concern, object? instance) => new(
            concern,
            instance?.GetType().Name ?? "not registered");

        // The connection string's host and database, with the credential
        // half dropped: a system page shows where data lives, not how to
        // reach it.
        private static string? PostgresTargetOf(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return null;
            string? host = null, database = null;
            foreach (var part in connectionString.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = part[..eq].Trim().ToLowerInvariant();
                var value = part[(eq + 1)..].Trim();
                if (key is "host" or "server")
                    host = value;
                else if (key is "database" or "db")
                    database = value;
            }
            return host is null && database is null
                ? "set (shape unrecognized)"
                : $"{host ?? "?"}/{database ?? "?"}";
        }
    }

    public sealed record StoreAdapter(string Concern, string Adapter);

    /// <summary>The host facts an operator correlating a deployment wants.</summary>
    public sealed record ServerSection(
        string Host,
        string OperatingSystem,
        string Runtime,
        DateTimeOffset StartedAt,
        DateTimeOffset Now)
    {
        public static ServerSection Of() => new(
            Environment.MachineName,
            $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            $".NET {Environment.Version} ({RuntimeInformation.FrameworkDescription})",
            Process.GetCurrentProcess().StartTime,
            DateTimeOffset.UtcNow);
    }
}
