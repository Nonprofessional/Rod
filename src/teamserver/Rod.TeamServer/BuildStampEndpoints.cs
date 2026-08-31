using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Rod.TeamServer;

/// <summary>
/// The build-report endpoint: <c>GET /build</c> returns the version and the exact
/// source commit this teamserver binary was built from (the generated
/// <see cref="BuildStamp"/>), so an operator can pin an installed server to its
/// source tree -- provenance that used to be a hand-declared build-source path
/// in configuration. The stamp names the running infrastructure, so it sits
/// behind operator authentication like the rest of the operator API; the
/// composition root maps it alongside the layer endpoints.
/// </summary>
public static class BuildStampEndpoints
{
    public static IEndpointRouteBuilder MapBuildStampEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/build", () => Results.Ok(new BuildReport(BuildStamp.Version, BuildStamp.SourceCommit)))
            .RequireAuthorization();
        return endpoints;
    }

    public sealed record BuildReport(string Version, string SourceCommit);
}
