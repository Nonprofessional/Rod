using System.Linq;
using System.Text;
using Rod.CoreState.Implants;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The stub build unit (roadmap M3.1): proves the build contract round-trip
/// without any real toolchain. The real in-tree reference build unit is .NET
/// (ADR 0009); community build units for other languages (Go/C/Nim) live
/// out-of-tree. The stub occupies the <see cref="Language.Go"/> slot so the
/// registry always has a deterministic, toolchain-free unit for tests and for
/// any language slot that has no in-tree unit.
///
/// It emits a deterministic, benign artifact: a UTF-8 manifest of the baked-in
/// config followed by a fixed, clearly-fake marker byte sequence. There is no
/// executable logic in the output -- by design (RESPONSIBLE-USE.md, AGENTS.md
/// Sec 7). The per-implant key never appears in the manifest; only its
/// fingerprint does, so a captured artifact does not leak the key material it
/// was built with.
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
        var verbs = string.Join(",", ImplantClassCapabilities.For(@params.Class));
        var beacon = @params.Beacon;
        // The malleable transport profile (architecture.md Sec 7): enroll path,
        // User-Agent, headers, request timeout, and body envelope are surfaced in
        // the manifest the same way the baked JSON surfaces them, so the stub
        // artifact is self-describing about its wire shape too.
        var transport = @params.Transport;
        // Headers render sorted by name so the stub manifest is deterministic
        // regardless of the runtime's dictionary iteration order, matching the
        // ordered render the Go and .NET units use for the baked JSON.
        var headers = transport.Headers.Count == 0
            ? "-"
            : string.Join(";", transport.Headers
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));
        return new StringBuilder()
            .AppendLine("# Rod implant build manifest (STUB -- not executable)")
            .AppendLine($"engagement={@params.EngagementId}")
            .AppendLine($"class={@params.Class}")
            .AppendLine($"verbs={verbs}")
            .AppendLine($"target={@params.Target.OperatingSystem}/{@params.Target.Architecture}")
            .AppendLine($"endpoint={transport.Endpoint}")
            .AppendLine($"uri={transport.UriPath}")
            .AppendLine($"enroll_path={transport.EnrollPath}")
            .AppendLine($"user_agent={transport.UserAgent}")
            .AppendLine($"headers={headers}")
            .AppendLine($"request_timeout={(long)transport.RequestTimeout.TotalSeconds}s")
            .AppendLine($"envelope={transport.Envelope.ToString().ToLowerInvariant()}")
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
