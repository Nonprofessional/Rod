namespace Rod.Audit;

/// <summary>
/// A first-class evidence object linked to a task (architecture.md Sec 11;
/// storage &amp; audit layer, roadmap M2.3). Files, screenshots, captured command
/// output, and the like are not loose files -- they are attributed, engagement-
/// scoped objects attached to the task that produced them, so the evidence and
/// the tasking that gathered it stay bound. The audit trail and the report
/// consumers (architecture.md Sec 11) read artifacts through this same scoping.
///
/// Like <see cref="AuditEvent"/>, an artifact carries plain <see cref="Guid"/>
/// identifiers rather than core-state typed ids: the audit layer is the innermost
/// ring and crosses the layer boundary with primitives, never core-state types.
/// </summary>
public sealed record Artifact(
    Guid ArtifactId,
    Guid EngagementId,
    Guid TaskId,
    Guid? OperatorId,
    string Name,
    string ContentType,
    byte[] Content,
    long Size,
    DateTimeOffset StoredAt);
