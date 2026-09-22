using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.Build.Tests;

/// <summary>
/// The Rust unit's refusal split: a wrong request throws the contract
/// exception (the HTTP-400 path), a broken environment throws the
/// build-failure exception (the HTTP-500 path). The two read identically at
/// the HTTP surface if they ever share a type again -- which is how a
/// missing musl target on a CI runner once read as three hundred bad
/// requests instead of one broken toolchain.
/// </summary>
public class RustBuildUnitTests
{
    private static BuildParams Params(ImplantClass @class = ImplantClass.Stage2) => new(
        EngagementId.New(),
        OperatorId.New(),
        @class,
        new TargetProfile("linux", "amd64"),
        new TransportProfile("http://c2.example.test", "/beacon"),
        new BeaconProfile(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddDays(30)));

    [Fact]
    public async Task MissingSourceTree_ThrowsBuildUnitFailure_NotAContractRefusal()
    {
        var unit = new RustBuildUnit(
            rustSourceDir: Path.Combine(Path.GetTempPath(), "rod-absent-" + Guid.NewGuid().ToString("N")));

        var ex = await Assert.ThrowsAsync<BuildUnitFailureException>(
            () => unit.BuildAsync(Params()));

        Assert.Contains("source tree not found", ex.Message);
    }

    [Fact]
    public async Task RetiredStagerClass_StillThrowsTheContractRefusal()
    {
        // The refusal precedes any filesystem touch, so the source dir never
        // needs to exist for this one.
        var unit = new RustBuildUnit(
            rustSourceDir: Path.Combine(Path.GetTempPath(), "rod-absent-" + Guid.NewGuid().ToString("N")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => unit.BuildAsync(Params(ImplantClass.Stager)));
    }
}
