using System.Text;
using Rod.CoreState.Implants;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The stub build unit (roadmap M3.1): proves the build contract round-trip
/// without any real toolchain. Real per-language build units arrive with M3.2
/// (Go) and M3.3 (.NET); until then this is the unit the registry resolves.
///
/// It emits a deterministic, benign artifact: a UTF-8 manifest of the baked-in
/// config followed by a fixed, clearly-fake marker byte sequence. There is no
/// executable logic in the output -- by design (RESPONSIBLE-USE.md, AGENTS.md
/// Sec 7). The per-implant key never appears in the manifest; only its
/// fingerprint does, so a captured artifact does not leak the key material it
/// was built with.
///
/// Registered under <see cref="Language.Go"/> as the default until the real Go
/// build unit (M3.2) takes that slot.
/// </summary>
public sealed class StubBuildUnit : IBuildUnit
{
    // A fixed, clearly-unexecutable marker so the artifact is recognizable as a
    // stub and never mistaken for a runnable payload. Not valid machine code on
    // any target; present only so the artifact has non-trivial bytes.
    private static readonly byte[] Marker =
        Encoding.UTF8.GetBytes("\n---ROD-STUB-BUILD-MARKER-NOT-EXECUTABLE---\n");

    public Language Language => Language.Go;

    public Task<BuildArtifact> BuildAsync(BuildParams @params, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = Encoding.UTF8.GetBytes(RenderManifest(@params, now));
        var content = Concat(manifest, Marker);

        var artifact = BuildArtifact.Of(
            Language,
            artifactId: Guid.NewGuid(),
            @params,
            content,
            contentType: "application/octet-stream",
            builtAt: now);

        return Task.FromResult(artifact);
    }

    // Renders the baked-in config as a stable, human-readable manifest. The key
    // itself is deliberately absent: only its fingerprint is recorded, so the
    // artifact cannot leak the per-implant key it carries.
    private static string RenderManifest(BuildParams @params, DateTimeOffset builtAt)
    {
        var keyFingerprint = ArtifactFingerprint.Of(Encoding.UTF8.GetBytes(@params.Key));
        var beacon = @params.Beacon;
        return new StringBuilder()
            .AppendLine("# Rod implant build manifest (STUB -- not executable)")
            .AppendLine($"engagement={@params.EngagementId}")
            .AppendLine($"class={@params.Class}")
            .AppendLine($"target={@params.Target.OperatingSystem}/{@params.Target.Architecture}")
            .AppendLine($"endpoint={@params.Transport.Endpoint}")
            .AppendLine($"uri={@params.Transport.UriPath}")
            .AppendLine($"sleep={(long)beacon.Sleep.TotalSeconds}s")
            .AppendLine($"jitter={(long)beacon.Jitter.TotalSeconds}s")
            .AppendLine($"kill_date={beacon.KillDate:O}")
            .AppendLine($"key_fingerprint={keyFingerprint}")
            .AppendLine($"built_at={builtAt:O}")
            .ToString();
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }
}
