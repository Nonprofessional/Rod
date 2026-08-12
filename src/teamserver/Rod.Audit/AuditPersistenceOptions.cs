namespace Rod.Audit;

/// <summary>
/// Where the durable audit/artifact adapters persist their evidence
/// (architecture.md Sec 11; roadmap M6.4). When the composition root binds this
/// from the <c>Audit</c> configuration section, the file-backed stores replace
/// the in-memory ones, so the engagement trail and its artifacts outlive a
/// teamserver restart and infrastructure teardown. Absent -- the default in the
/// test host and any host that does not opt in -- the in-memory adapters stay in
/// place, unchanged.
///
/// The durable adapters are the walking-skeleton stand-in for the eventual
/// Postgres-backed audit store (architecture.md Sec 12): JSON Lines on a local
/// directory instead of a managed database, behind the same ports. Like the
/// other adapters, the layer stays a zero-package classlib -- <c>System.Text.Json</c>
/// and <c>System.IO</c> are BCL, so no in-house dependency is introduced and the
/// inner-ring architecture rule (<c>Audit_Dependencies_PointInwardOnly</c>) holds.
/// </summary>
public sealed class AuditPersistenceOptions
{
    /// <summary>
    /// The directory the durable stores write into. A relative path is resolved
    /// against the host's current working directory (the teamserver content root
    /// for <c>dotnet run</c>). The directory and its <c>blobs/</c> subdirectory
    /// are created lazily on first write.
    /// </summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>True when <see cref="DataDirectory"/> has been configured.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(DataDirectory);
}
