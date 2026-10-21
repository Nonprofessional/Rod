using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Rod.Transport;

/// <summary>
/// The versioned face of the operator API (architecture.md Sec 15.3, ADR-006):
/// <c>/api/v1</c> is a prefix alias over the operator surface. The operator
/// endpoints keep their long-standing unversioned routes -- the console, the
/// integration suites, and every bookmarked workflow keep working -- while
/// programmatic consumers (the official SDKs, future <c>/api/v2</c> surfaces)
/// address one stable, versioned URL space. The alias rewrites before routing,
/// so the versioned request resolves against the real route table; when a
/// breaking v2 ever exists, it mounts as real routes beside this alias instead
/// of through it.
/// </summary>
/// <remarks>
/// The alias strips exactly <c>/api/v1</c> and nothing else, so the wire-facing
/// surfaces (enrollment, the beacon carriages, the public hook and lure edges)
/// stay unreachable through the versioned prefix unless they choose to be: no
/// operator route moves, and no implant route gains a second name. The
/// OpenAPI document describes the operator API with <c>/api/v1</c> as its
/// server base URL, so spec consumers see the versioned space.
/// </remarks>
public static class OperatorApiVersionAlias
{
    /// <summary>The versioned prefix the alias serves.</summary>
    public const string Prefix = "/api/v1";

    /// <summary>
    /// Rewrites <c>/api/v1/&lt;path&gt;</c> to <c>/&lt;path&gt;</c> before routing.
    /// Register ahead of <c>UseRouting</c>: after route resolution a path
    /// change selects nothing.
    /// </summary>
    public static IApplicationBuilder UseOperatorApiVersionAlias(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.HasValue &&
                path.Value!.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                context.Request.Path = path.Value[Prefix.Length..];
            }
            await next(context);
        });
}
