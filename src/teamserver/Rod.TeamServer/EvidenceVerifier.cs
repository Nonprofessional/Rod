using Rod.Audit;

namespace Rod.TeamServer;

/// <summary>
/// The offline evidence verifier (architecture.md Sec 11, Sec 2 step 10): the
/// close-out's acceptance step, run as
/// <c>Rod.TeamServer --verify-evidence &lt;package.zip&gt;</c> on a host with no
/// Rod infrastructure running -- no listeners, no stores, just the binary and
/// the exported package. Exits 0 when the package re-verifies byte-exact
/// (manifest digests, the audit hash chain, the artifact records) and 1 naming
/// the first failure otherwise.
/// </summary>
internal static class EvidenceVerifier
{
    public static async Task<int> RunAsync(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"rod-teamserver: no such file: {path}");
            return 1;
        }

        await using var stream = File.OpenRead(path);
        var verification = await EvidencePackage.VerifyAsync(stream);
        if (verification.Verified && verification.Manifest is { } manifest)
        {
            Console.WriteLine(
                $"verified: engagement {manifest.Header.EngagementId:N} ({manifest.Header.EngagementName}), " +
                $"exported {manifest.Header.ExportedAt:O} by operator {manifest.Header.ExportedBy:N}");
            Console.WriteLine(
                $"  events: {manifest.EventCount}, artifacts: {manifest.ArtifactCount}, " +
                $"files: {manifest.Files.Count + 1} (including the manifest)");
            return 0;
        }

        Console.Error.WriteLine($"verification FAILED: {verification.Failure}");
        return 1;
    }
}
