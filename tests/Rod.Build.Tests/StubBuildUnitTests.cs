using System.Security.Cryptography;
using System.Text;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.Build.Tests;

/// <summary>
/// Unit tests for the stub build unit (roadmap M3.1): the build contract's
/// benign producer. Proves the stub is deterministic, non-empty, correctly
/// fingerprinted, and that the per-implant key never appears in the artifact --
/// only its fingerprint does. Keeps build-layer coverage in the Build.Tests
/// project alongside the CPM guard.
/// </summary>
public class StubBuildUnitTests
{
    private static BuildParams Params(string key) => new(
        EngagementId.New(),
        OperatorId.New(),
        ImplantClass.Stage2,
        new TargetProfile("linux", "amd64"),
        new TransportProfile("http://c2.example.test", "/beacon"),
        new BeaconProfile(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddDays(30)),
        key);

    [Fact]
    public async Task Build_ReturnsNonEmptyArtifact_WithGoLanguage()
    {
        var unit = new StubBuildUnit();

        var artifact = await unit.BuildAsync(Params("key-one"));

        Assert.Equal(Language.Go, artifact.Language);
        Assert.NotEmpty(artifact.Content);
        Assert.Equal(artifact.Content.Length, artifact.Size);
        Assert.False(string.IsNullOrWhiteSpace(artifact.ContentType));
    }

    [Fact]
    public async Task Build_Fingerprint_MatchesSha256OfContent()
    {
        var unit = new StubBuildUnit();

        var artifact = await unit.BuildAsync(Params("key-one"));

        var expected = Convert.ToHexString(SHA256.HashData(artifact.Content)).ToLowerInvariant();
        Assert.Equal(expected, artifact.Fingerprint);
        Assert.Equal(64, artifact.Fingerprint.Length);
    }

    [Fact]
    public async Task Build_DoesNotLeak_PerImplantKey()
    {
        // The artifact must carry only the key's fingerprint, never the key
        // itself -- a captured artifact must not leak the material it was built
        // with (architecture.md Sec 7).
        var unit = new StubBuildUnit();
        var key = "super-secret-per-implant-key-value";

        var artifact = await unit.BuildAsync(Params(key));

        var manifest = Encoding.UTF8.GetString(artifact.Content);
        Assert.DoesNotContain(key, manifest);
        // ...but the key's fingerprint is recorded, so the artifact is traceable.
        var keyFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        Assert.Contains(keyFingerprint, manifest);
    }

    [Fact]
    public void Registry_ResolvesUnit_ByLanguage()
    {
        var registry = new InMemoryBuildUnitRegistry();
        registry.Register(new StubBuildUnit());

        var resolved = registry.Find(Language.Go);

        Assert.NotNull(resolved);
        Assert.IsType<StubBuildUnit>(resolved);
    }

    [Fact]
    public void Registry_ReturnsNull_ForUnregisteredLanguage()
    {
        var registry = new InMemoryBuildUnitRegistry();

        Assert.Null(registry.Find(Language.Go));
    }
}
