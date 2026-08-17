namespace Rod.Transport.Endpoints;

/// <summary>
/// The naming convention that binds a staged payload's artifact to its task
/// (architecture.md Sec 10, the per-verb typed arm). The issue endpoint names
/// the artifact it stages and the beacon stream's writer finds it by the same
/// name, so the two ends of the demand path share one definition instead of a
/// duplicated string. An operator's evidence attaches use their own names and
/// are never mistaken for the staged payload.
/// </summary>
internal static class StagedArtifacts
{
    /// <summary>The artifact name for a staged task's payload.</summary>
    public static string NameFor(Guid taskId) => "staged-" + taskId.ToString("N");
}
