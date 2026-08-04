using System.Security.Cryptography;
using System.Text;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.Build.Tests;

/// <summary>
/// Unit tests for the real Go build unit (roadmap M3.2): the contract's Go
/// producer. Proves the unit compiles a non-empty, fingerprinted artifact for the
/// configured target, that two builds of the same params never share a
/// fingerprint (per-implant material is generated at request time), and that the
/// baked profile does not leak the per-implant key into the artifact. Skipped
/// when go is not on PATH.
/// </summary>
public class GoBuildUnitTests
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
        // The baked profile carries only the key's fingerprint, never the key
        // itself -- a captured artifact must not leak the material it was built
        // with (architecture.md Sec 7).
        var key = "super-secret-per-implant-key-value";
        var baked = GoBuildUnit.RenderBakedProfile(Params(key));

        Assert.DoesNotContain(key, baked);
    }

    [Fact]
    public void RenderBakedProfile_IncludesKeyFingerprint()
    {
        // The baked profile is base64url-encoded JSON; decode it back and confirm
        // the key's fingerprint is recorded there (a captured artifact is traceable
        // to its key without the key itself leaking).
        var key = "another-key";
        var baked = GoBuildUnit.RenderBakedProfile(Params(key));

        var json = Encoding.UTF8.GetString(Base64UrlDecode(baked));

        var keyFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        Assert.Contains(keyFingerprint, json);
    }

    [Theory]
    [InlineData(ImplantClass.Stage2, "shell.exec,file.push,file.pull,tunnel.open,probe.read")]
    [InlineData(ImplantClass.Stager, "file.pull")]
    [InlineData(ImplantClass.Pivot, "tunnel.open,probe.read")]
    public void RenderBakedProfile_BakesTheClassReducedVerbSet(ImplantClass @class, string expectedVerbs)
    {
        // The class's reduced verb set (architecture.md Sec 5.2) is baked into
        // the profile, so the generated implant carries the verbs it may run.
        var baked = GoBuildUnit.RenderBakedProfile(Params("key-one", @class));

        var json = Encoding.UTF8.GetString(Base64UrlDecode(baked));

        Assert.Contains($"\"verbs\":\"{expectedVerbs}\"", json);
    }

    // RFC 4648 base64url without padding, matching GoBuildUnit.Base64Url.
    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) & ~3, '=');
        return Convert.FromBase64String(padded);
    }

    [GoFact]
    public async Task Build_ReturnsNonEmptyArtifact_WithGoLanguage()
    {
        var unit = new GoBuildUnit();

        var artifact = await unit.BuildAsync(Params("key-one"));

        Assert.Equal(Language.Go, artifact.Language);
        Assert.NotEmpty(artifact.Content);
        Assert.Equal(artifact.Content.Length, artifact.Size);
        Assert.Equal("application/octet-stream", artifact.ContentType);
    }

    [GoFact]
    public async Task Build_Fingerprint_MatchesSha256OfContent()
    {
        var unit = new GoBuildUnit();

        var artifact = await unit.BuildAsync(Params("key-one"));

        var expected = Convert.ToHexString(SHA256.HashData(artifact.Content)).ToLowerInvariant();
        Assert.Equal(expected, artifact.Fingerprint);
        Assert.Equal(64, artifact.Fingerprint.Length);
    }

    [GoFact]
    public async Task TwoBuilds_WithIdenticalParams_ProduceDifferentFingerprints()
    {
        // Each build bakes a fresh artifact over a fresh temp dir; even with the
        // same params the build embeds non-determinism (build id), so two builds
        // never share a fingerprint -- matching the per-implant uniqueness
        // contract (architecture.md Sec 5.1/6).
        var unit = new GoBuildUnit();

        var first = await unit.BuildAsync(Params("key-one"));
        var second = await unit.BuildAsync(Params("key-one"));

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }
}
