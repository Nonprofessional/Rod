using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.Build.Tests;

/// <summary>
/// Direct checks of <see cref="PayloadBuildService"/> -- the teamserver-side
/// orchestrator that drives build units. Focuses on the
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

    [Fact]
    public async Task Build_FlowsTheMalleableTransportProfile_ToTheArtifact()
    {
        // The malleable transport knobs set on the request (architecture.md
        // Sec 7) flow BuildRequest -> BuildParams -> baked artifact unchanged, so an
        // operator profile is reflected in the generated payload. The stub manifest
        // is UTF-8 text, so a substring check is enough; the per-knob round trip is
        // covered in the build-unit tests, this proves the orchestrator carries it.
        var service = NewService();
        var transport = new TransportProfile("http://c2.example.test/implants/enroll", "/beacon")
        {
            EnrollPath = "/api/v1/health",
            UserAgent = "Mozilla/5.0 (RodTest)",
            Headers = new Dictionary<string, string> { ["Accept"] = "application/json" },
            RequestTimeout = TimeSpan.FromSeconds(12),
            Envelope = TransportEnvelope.Base64,
        };
        var request = new BuildRequest(
            EngagementId.New(),
            OperatorId.New(),
            Language.Go,
            ImplantClass.Stage2,
            new TargetProfile("linux", "amd64"),
            transport,
            Sleep: TimeSpan.FromSeconds(30),
            Jitter: TimeSpan.FromSeconds(10),
            KillDate: null);

        var artifact = await service.BuildAsync(request);

        Assert.Same(transport, artifact.Params.Transport);
        var manifest = System.Text.Encoding.UTF8.GetString(artifact.Content);
        Assert.Contains("enroll_path=/api/v1/health", manifest);
        Assert.Contains("user_agent=Mozilla/5.0 (RodTest)", manifest);
        Assert.Contains("headers=Accept=application/json", manifest);
        Assert.Contains("request_timeout=12s", manifest);
        Assert.Contains("envelope=base64", manifest);
    }
}
