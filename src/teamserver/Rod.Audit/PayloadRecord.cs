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
public sealed record PayloadRecord(
    Guid PayloadId,
    Guid EngagementId,
    string Class,
    string Language,
    string ContentType,
    string Fingerprint,
    byte[] Content,
    long Size,
    DateTimeOffset BuiltAt);