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
    private static BuildParams Params(ImplantClass @class = ImplantClass.Implant) => new(
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

    // The loader tier's contract refusals: the dial must be a literal IPv4
    // over cleartext http (the no_std dialer parses nothing else), and the
    // seal pair and stage reference must ride the params -- the endpoint
    // mints them, and a request that reaches the unit without them is a
    // contract violation, not a toolchain fault.
    [Fact]
    public async Task ALoaderDialThatIsNotALiteralIpv4Http_IsAContractRefusal()
    {
        var unit = new RustBuildUnit();
        var @params = Params() with
        {
            Kind = PayloadKind.Loader,
            StagePayloadId = Guid.NewGuid(),
            TokenSecret = "loader-probe",
            EnvelopeKeyId = Guid.NewGuid(),
            EnvelopeKey = new byte[32],
            Transport = new TransportProfile("https://front.example.test", "/beacon"),
        };

        var https = await Assert.ThrowsAsync<InvalidOperationException>(() => unit.BuildAsync(@params));
        Assert.Contains("cleartext http front", https.Message);

        @params = @params with { Transport = new TransportProfile("http://front.example.test", "/beacon") };
        var hostname = await Assert.ThrowsAsync<InvalidOperationException>(() => unit.BuildAsync(@params));
        Assert.Contains("literal IPv4", hostname.Message);
    }

    [Fact]
    public async Task ALoaderWithoutItsSealKey_IsAContractRefusal()
    {
        var unit = new RustBuildUnit();
        var @params = Params() with
        {
            Kind = PayloadKind.Loader,
            StagePayloadId = Guid.NewGuid(),
            TokenSecret = "loader-probe",
            Transport = new TransportProfile("http://10.0.0.9:8080", "/beacon"),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => unit.BuildAsync(@params));
        Assert.Contains("stage seal key", ex.Message);
    }

    // The real compile: builds the loader crate for the host's musl target
    // and checks the two facts the delivery depends on -- the baked fetch
    // credential rides the binary verbatim (the dial presents it), and the
    // artifact stays in the tier's size class (a blown-up binary means the
    // no-libc link went wrong). Skipped without the toolchain, the same
    // environment gate the system page reports.
    [Fact]
    public async Task ALoaderBuild_BakesTheCredential_AndStaysSmall()
    {
        var unit = new RustBuildUnit();
        if (unit.ReportEnvironment().Status is "unavailable" or null)
            return; // no toolchain on this runner; the environment report names the fix

        var secret = "stage-credential-probe";
        var @params = Params() with
        {
            Kind = PayloadKind.Loader,
            StagePayloadId = Guid.NewGuid(),
            TokenSecret = secret,
            EnvelopeKeyId = Guid.NewGuid(),
            EnvelopeKey = new byte[32],
            Transport = new TransportProfile("http://127.0.0.1:8080", "/beacon"),
        };

        var artifact = await unit.BuildAsync(@params);

        Assert.Equal(PayloadKind.Loader, artifact.Params.Kind);
        // The token is baked as a plain string constant; its bytes are the
        // cheapest proof the bake reached the binary.
        Assert.True(
            artifact.Content.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(secret)) >= 0,
            "the baked credential rides the loader binary");
        Assert.True(artifact.Size < 512 * 1024, $"loader weighed {artifact.Size} bytes");
    }

}
