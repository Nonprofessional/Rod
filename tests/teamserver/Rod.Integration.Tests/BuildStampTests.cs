using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Rod.TeamServer;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: the teamserver binary self-reports its provenance. The build
/// stamps the exact source commit into the binary (Directory.Build.targets), the
/// startup log names it, and <c>GET /build</c> returns the pair to an
/// authenticated operator -- so an installed server is pinned to its source tree
/// without trusting a hand-declared path in configuration. The report names the
/// running infrastructure, so it must not answer anonymously.
/// </summary>
public partial class BuildStampTests
{
    [Fact]
    public async Task Build_Report_Is_Rejected_Anonymously()
    {
        var (client, host, _) = AuthenticatedHost.Create(
            mapEndpoints: MapBuildStamp);
        using var _ = host;

        var response = await client.GetAsync("/build");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Build_Report_Returns_The_Stamped_Version_And_Commit()
    {
        var (client, host, _) = AuthenticatedHost.Create(
            mapEndpoints: MapBuildStamp);
        using var _ = host;
        await AuthenticatedHost.LoginAsync(client);

        var report = await client.GetFromJsonAsync<BuildStampEndpoints.BuildReport>("/build");

        Assert.NotNull(report);
        // The endpoint reports exactly what was stamped into the binary, so the
        // two surfaces (log line and endpoint) can never drift apart.
        Assert.Equal(BuildStamp.Version, report.Version);
        Assert.Equal(BuildStamp.SourceCommit, report.SourceCommit);
        // A version always carries the product shape -- a bare x.y.z claims the
        // released version, so a development tree carries a pre-release suffix
        // (RodVersion in Directory.Build.props); the commit is either form.
        Assert.Matches(VersionShape(), report.Version);
        Assert.Matches(CommitShape(), report.SourceCommit);
    }

    private static void MapBuildStamp(IEndpointRouteBuilder endpoints)
        => endpoints.MapBuildStampEndpoints();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-[\w.]+)?$")]
    private static partial Regex VersionShape();

    [GeneratedRegex("^(unknown|[0-9a-f]{40})$")]
    private static partial Regex CommitShape();
}
