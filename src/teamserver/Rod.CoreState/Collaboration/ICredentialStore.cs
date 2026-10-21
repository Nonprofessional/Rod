namespace Rod.CoreState.Collaboration;

/// <summary>
/// One harvested-credential row, persisted (architecture.md Sec 15.3,
/// ADR-005): the landing of a credential-kind task's output line, with the
/// stable id that makes the row addressable by a task-reference placeholder
/// (<c>$cred-&lt;id&gt;</c>) and searchable. The secret column carries what
/// the implant reported -- the same plaintext posture the task transcript
/// already holds, never enriched here -- and it never enters the audit
/// chain and never enters a search index: search reads the metadata
/// columns, not the secret.
/// </summary>
public sealed record CredentialRecord(
    CredentialId Id,
    EngagementId EngagementId,
    TaskId SourceTaskId,
    ImplantId SourceImplantId,
    string Kind,
    string Principal,
    string? Secret,
    string? Source,
    string? Host,
    string? Note,
    DateTimeOffset CapturedAt);

/// <summary>
/// The durable home for harvested credentials (architecture.md Sec 15.3,
/// ADR-005). Rows land once, when the capturing task completes; there is no
/// lifecycle yet (rotation marks are the entity-model item's work, Sec
/// 15.4). In-memory by default, Postgres-backed when the connection string
/// is set.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Adds a row (rows are append-only; the id is the identity).</summary>
    Task AddAsync(CredentialRecord record, CancellationToken cancellationToken = default);

    /// <summary>The row, or null when unknown.</summary>
    Task<CredentialRecord?> FindAsync(CredentialId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The engagement's rows, oldest capture first -- the credential
    /// panel's read.
    /// </summary>
    Task<IReadOnlyList<CredentialRecord>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The rows one task's harvest landed, so a re-run capture can tell it
    /// already stored them.
    /// </summary>
    Task<IReadOnlyList<CredentialRecord>> ListBySourceTaskAsync(
        EngagementId engagementId,
        TaskId taskId,
        CancellationToken cancellationToken = default);
}
