namespace Rod.Audit;

/// <summary>
/// Artifact store port (architecture.md Sec 11; storage &amp; audit layer, roadmap
/// M2.3). Artifacts are first-class objects attached to tasks; this port is the
/// evidence backbone alongside <see cref="IAuditStore"/>. The walking skeleton
/// ships an in-memory implementation; the port keeps callers agnostic to that.
///
/// The audit layer is the innermost ring (architecture.md Sec 4.1): it depends
/// on nothing in-house, so artifacts carry plain <see cref="Guid"/> identifiers
/// rather than core-state typed ids -- the layer boundary is crossed with
/// primitives. Engagement scoping is the caller's discipline (the operator API
/// resolves the engagement and passes its id in); <see cref="ListAsync"/> and
/// <see cref="ForTaskAsync"/> filter on it, so cross-engagement access never
/// returns another engagement's artifacts by construction.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Saves <paramref name="artifact"/>. The artifact becomes retrievable by id,
    /// by its task, and within its engagement.
    /// </summary>
    Task SaveAsync(Artifact artifact, CancellationToken cancellationToken = default);

    /// <summary>An artifact by id, or null when no artifact has that id.</summary>
    Task<Artifact?> FindAsync(Guid artifactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Artifacts attached to a task, oldest first -- the evidence gathered by
    /// that task's execution.
    /// </summary>
    Task<IReadOnlyList<Artifact>> ForTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The engagement's artifacts, oldest first. Per-engagement by construction;
    /// cross-engagement access never reaches this with another engagement's id.
    /// </summary>
    Task<IReadOnlyList<Artifact>> ListAsync(Guid engagementId, CancellationToken cancellationToken = default);
}
