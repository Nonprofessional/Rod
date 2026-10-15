namespace Rod.Audit;

/// <summary>
/// Where the durable audit/artifact adapters persist their evidence
/// (architecture.md Sec 11; ). When the composition root binds this
/// from the <c>Audit</c> configuration section, the file-backed stores replace
/// the in-memory ones, so the engagement trail and its artifacts outlive a
/// teamserver restart and infrastructure teardown. Absent -- the default in the
/// test host and any host that does not opt in -- the in-memory adapters stay in
/// place, unchanged.
///
/// The durable adapters stand in for the eventual
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

    /// <summary>
    /// The optional chain key (from <c>Audit:ChainKey</c>, base64): when held,
    /// audit links are HMAC-SHA256 instead of bare SHA-256, so an attacker
    /// with write access to the storage alone cannot rewrite a trail
    /// consistently (see <see cref="AuditChain"/> for the boundary this
    /// draws and where it honestly stops). Null keeps the unkeyed chain
    /// every existing trail already carries.
    /// </summary>
    public byte[]? ChainKey { get; set; }

    /// <summary>
    /// Parses the configured chain key: null or blank stays null (unkeyed),
    /// anything else must be base64 -- a malformed key fails loudly rather
    /// than silently disabling the keyed mode an operator believes they
    /// configured.
    /// </summary>
    public static byte[]? ParseChainKey(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;
        try
        {
            return Convert.FromBase64String(configured.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "Audit:ChainKey must be base64-encoded key material (32 bytes is the expected size).");
        }
    }
}
