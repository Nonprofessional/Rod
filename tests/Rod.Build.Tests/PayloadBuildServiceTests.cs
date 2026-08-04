using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.Build.Tests;

/// <summary>
/// Direct checks of <see cref="PayloadBuildService"/> -- the teamserver-side
/// orchestrator that drives build units (roadmap M3.1). Focuses on the
/// per-implant material the service generates at request time (architecture.md
/// Sec 6/Sec 5.1), which the build-unit tests read back only indirectly through
/// the baked profile.
/// </summary>
public class PayloadBuildServiceTests
{
    private static BuildRequest Request() => new(
        EngagementId.New(),
        OperatorId.New(),
        Language.Go,
        ImplantClass.Stage2,
        new TargetProfile("linux", "amd64"),
        new TransportProfile("http://c2.example.test/implants/enroll", "/beacon"),
        Sleep: TimeSpan.FromSeconds(30),
        Jitter: TimeSpan.FromSeconds(10),
        KillDate: null);

    // A payload build service with the stub build unit registered, so the test
    // exercises the real orchestrator (key generation, kill-date resolution,
    // build-unit dispatch) without a Go/.NET toolchain.
    private static PayloadBuildService NewService()
    {
        var registry = new InMemoryBuildUnitRegistry();
        registry.Register(new StubBuildUnit());
        return new PayloadBuildService(registry, TimeProvider.System);
    }

    [Fact]
    public async Task Build_GeneratesDistinctKeys_PerRequest()
    {
        // The per-implant key is generated at request time (architecture.md Sec 6,
        // Sec 7) so two builds never share material -- compromising one artifact
        // must not compromise another. Two builds of the same request therefore
        // carry different keys; a regression to a shared or constant key would
        // fail this.
        var service = NewService();

        var first = await service.BuildAsync(Request());
        var second = await service.BuildAsync(Request());

        Assert.NotEmpty(first.Params.Key);
        Assert.NotEmpty(second.Params.Key);
        Assert.NotEqual(first.Params.Key, second.Params.Key);
    }

    [Fact]
    public async Task Build_RecordsTheKeyFingerprint_NotTheKey_InTheArtifact()
    {
        // The artifact carries only the key's fingerprint, never the key itself
        // (architecture.md Sec 7): a captured artifact must not leak the material
        // it was built with. The stub manifest is UTF-8 text, so a plain substring
        // check is enough.
        var service = NewService();

        var artifact = await service.BuildAsync(Request());

        var manifest = System.Text.Encoding.UTF8.GetString(artifact.Content);
        Assert.DoesNotContain(artifact.Params.Key, manifest);
    }
}
