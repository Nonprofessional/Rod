using Rod.CoreState;
using Rod.CoreState.Implants;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The payload-build result schema -- the build unit half of the build contract
/// (architecture.md Sec 6). A compiled (stub, for now) artifact returned to the
/// teamserver, fingerprinted and ready to be recorded. The fingerprint is a
/// SHA-256 hex digest over <see cref="Content"/>; <see cref="Size"/> is its byte
/// length. <see cref="Params"/> is the build request that produced this artifact,
/// carried back so the recorder has the full who/when/config alongside the
/// fingerprint (architecture.md Sec 11). <see cref="Language"/> records which
/// build unit compiled it -- the contract input is language-neutral, so the unit
/// stamps the language on the result.
/// </summary>
public sealed record BuildArtifact(
    Guid ArtifactId,
    EngagementId EngagementId,
    ImplantClass Class,
    Language Language,
    byte[] Content,
    string ContentType,
    string Fingerprint,
    long Size,
    DateTimeOffset BuiltAt,
    BuildParams Params)
{
    /// <summary>
    /// Builds a result from compiled <paramref name="content"/>, computing the
    /// fingerprint and size. Build units call this rather than hand-filling the
    /// fields, so the fingerprint always agrees with the bytes.
    /// </summary>
    public static BuildArtifact Of(
        Language language,
        Guid artifactId,
        BuildParams @params,
        byte[] content,
        string contentType,
        DateTimeOffset builtAt)
    {
        var fingerprint = ArtifactFingerprint.Of(content);
        return new BuildArtifact(
            artifactId,
            @params.EngagementId,
            @params.Class,
            language,
            content,
            contentType,
            fingerprint,
            content.LongLength,
            builtAt,
            @params);
    }
}
