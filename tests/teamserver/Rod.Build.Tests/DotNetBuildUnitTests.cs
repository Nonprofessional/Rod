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
/// Unit tests for the real .NET build unit (roadmap M3.3): the contract's .NET
/// producer. Proves the unit compiles a non-empty, fingerprinted artifact for the
/// configured target, that two builds of the same params never share a
/// fingerprint (per-implant material is generated at request time), and that the
/// baked profile does not leak the per-implant key into the artifact. The
/// per-build tests are skipped when dotnet is not on PATH.
/// </summary>
public class DotNetBuildUnitTests
{
    private static BuildParams Params(string key, ImplantClass @class = ImplantClass.Stage2) => new(
        EngagementId.New(),
        OperatorId.New(),
        @class,
        new TargetProfile("linux", "amd64"),
        new TransportProfile("http://c2.example.test/implants/enroll", "/beacon"),
        new BeaconProfile(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddDays(30)),
        key);

    [Fact]
    public void RenderBakedProfile_DoesNotLeak_PerImplantKey()
    {
        // No key material is baked: neither the key itself nor any derived value
        // (fingerprint included) lands in the artifact, so a captured payload
        // cannot leak what it was built with (architecture.md Sec 7).
        var key = "super-secret-per-implant-key-value";
        var baked = DotNetBuildUnit.RenderBakedProfile(Params(key));

        Assert.DoesNotContain(key, baked);
        var json = Encoding.UTF8.GetString(Base64UrlDecode(baked));
        var keyFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        Assert.DoesNotContain(keyFingerprint, json);
    }

    [Theory]
    [InlineData(ImplantClass.Stage2, "shell.exec,file.push,file.pull,tunnel.open,probe.read,recon.portscan,recon.hostenum,recon.service,lateral.move,lateral.token,lateral.exec_remote,persist.install,persist.remove,persist.list,collect.file,collect.cred,collect.keylog,exfil.push,exfil.stage")]
    [InlineData(ImplantClass.Stager, "file.pull")]
    [InlineData(ImplantClass.Pivot, "tunnel.open,probe.read")]
    public void RenderBakedProfile_BakesTheClassReducedVerbSet(ImplantClass @class, string expectedVerbs)
    {
        // The class's reduced verb set (architecture.md Sec 5.2) is baked into
        // the profile, so the generated implant carries the verbs it may run.
        var baked = DotNetBuildUnit.RenderBakedProfile(Params("key-one", @class));

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
            new BeaconProfile(sleep, jitter, killDate),
            "key-one");

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
        // The malleable transport profile (architecture.md Sec 7, M4.3) must land
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
            new BeaconProfile(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddDays(30)),
            "key-one");

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

    // RFC 4648 base64url without padding, matching DotNetBuildUnit.Base64Url.
    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) & ~3, '=');
        return Convert.FromBase64String(padded);
    }

    [DotNetFact]
    public async Task Build_ReturnsNonEmptyArtifact_WithDotNetLanguage()
    {
        var unit = new DotNetBuildUnit();

        var artifact = await unit.BuildAsync(Params("key-one"));

        Assert.Equal(Language.DotNet, artifact.Language);
        Assert.NotEmpty(artifact.Content);
        Assert.Equal(artifact.Content.Length, artifact.Size);
        Assert.Equal("application/octet-stream", artifact.ContentType);
    }

    [DotNetFact]
    public async Task Build_Fingerprint_MatchesSha256OfContent()
    {
        var unit = new DotNetBuildUnit();

        var artifact = await unit.BuildAsync(Params("key-one"));

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

        var first = await unit.BuildAsync(Params("key-one"));
        var second = await unit.BuildAsync(Params("key-one"));

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }
}
