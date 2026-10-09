namespace Rod.Audit;

/// <summary>
/// A first-class evidence object (architecture.md Sec 11; storage &amp; audit
/// layer). Files, screenshots, captured command output, and the like are not
/// loose files -- they are attributed, engagement-scoped objects, so the
/// evidence and the action that gathered it stay bound. Most artifacts are
/// joined to the task that produced them; the ones that predate any foothold
/// (the recon workbench's findings, Sec 11.4) carry no task -- their task
/// reference is null and their attribution is the acting operator. The audit
/// trail and the report consumers (architecture.md Sec 11) read artifacts
/// through this same scoping.
///
/// Like <see cref="AuditEvent"/>, an artifact carries plain <see cref="Guid"/>
/// identifiers rather than core-state typed ids: the audit layer is the innermost
/// ring and crosses the layer boundary with primitives, never core-state types.
/// </summary>
public sealed record Artifact(
    Guid ArtifactId,
    Guid EngagementId,
    Guid? TaskId,
    Guid? OperatorId,
    string Name,
    string ContentType,
    byte[] Content,
    long Size,
    DateTimeOffset StoredAt);
