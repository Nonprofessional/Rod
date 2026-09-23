using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.BuildPipeline.PayloadBuild;

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

    private static IResult GetSystemAsync(IBuildUnitRegistry buildUnits)
    {
        // Only units that can probe themselves report; an out-of-tree unit
        // without the capability is a silent row, not a fabricated one.
        var units = buildUnits.All
            .OfType<IBuildUnitEnvironment>()
            .Select(u => u.ReportEnvironment())
            .ToArray();
        return Results.Ok(new SystemInfoResponse(ServerSection.Of(), units));
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record SystemInfoResponse(ServerSection Server, IReadOnlyList<BuildUnitEnvironmentReport> BuildUnits);

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
