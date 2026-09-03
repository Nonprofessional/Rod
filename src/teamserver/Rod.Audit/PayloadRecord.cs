namespace Rod.Audit;

/// <summary>
/// A built implant payload awaiting retrieval by the operator (architecture.md
/// Sec 6; storage &amp; audit layer). Payloads are engagement-scoped evidence-grade
/// objects like <see cref="Artifact"/>, but they are not attached to a task: a
/// payload is generated before any implant enrolls, so it carries the engagement
/// and the requesting operator's build configuration instead of a task binding.
/// <see cref="Class"/> and <see cref="Language"/> are kept as strings so the
/// audit layer stays free of core-state and build-pipeline types -- the
/// innermost ring crosses the layer boundary with primitives only.
/// </summary>
/// <param name="Target">
/// The build target as <c>os/arch</c> (e.g. <c>linux/amd64</c>), for the
/// operator's library view and filtering. Null on records written before the
/// field existed.
/// </param>
/// <param name="Endpoint">
/// The dial address baked into the artifact (the listener's public endpoint),
/// so the library reads which front a payload phones. Null on old records.
/// </param>
/// <param name="TokenId">
/// The enrollment credential minted for and baked into this artifact: enough
/// to revoke it from the library view, never enough to reuse it. Null when the
/// build was credential-free, or on old records.
/// </param>
public sealed record PayloadRecord(
    Guid PayloadId,
    Guid EngagementId,
    string Class,
    string Language,
    string ContentType,
    string Fingerprint,
    byte[] Content,
    long Size,
    DateTimeOffset BuiltAt,
    string? Target = null,
    string? Endpoint = null,
    Guid? TokenId = null);
