namespace Rod.Operators.Workbench;

/// <summary>
/// Configuration for the external recon workbench (architecture.md
/// Sec 11.4). Opt-in by construction, one gate per egress: even a passive
/// lookup leaves the teamserver under its own address carrying the target's
/// name, so which service each half rides is the operator's call, never a
/// silent default -- an unset base URL means the route answers 503 naming
/// this section, the LLM client's discipline (Sec 11.3). The scan carries
/// one further decision the lookups do not: where it originates. The only
/// shipped origin is the teamserver itself, and it must be named
/// (<see cref="ScanOrigin"/> = <c>Teamserver</c>) or the scan route
/// answers 503 too. The runbook records the tradeoffs and the fronting
/// options (docs/operations/recon.md).
/// </summary>
public sealed class ReconWorkbenchOptions
{
    public const string SectionName = "Recon";

    /// <summary>
    /// The only scan origin the teamserver ships: the scan's connections
    /// egress from the teamserver process itself. Any other value is
    /// refused with a 503 naming the runbook -- a redirector- or
    /// implant-originated scan is a future choice with its own decision,
    /// not a silent fallback.
    /// </summary>
    public const string TeamserverOrigin = "Teamserver";

    /// <summary>
    /// The RDAP service the workbench queries, e.g. a public redirector
    /// (<c>https://rdap.org</c>), a registry-direct endpoint, or a fronted
    /// mirror. Requests go to <c>{base}/domain/{name}</c>.
    /// </summary>
    public string? RdapBaseUrl { get; set; }

    /// <summary>
    /// The certificate-transparency mirror the workbench queries -- any
    /// endpoint answering the crt.sh JSON shape
    /// (<c>{base}/?q=%.{domain}&amp;output=json</c>).
    /// </summary>
    public string? CtBaseUrl { get; set; }

    /// <summary>
    /// The scan's origin, naming <see cref="TeamserverOrigin"/> to arm the
    /// scan route. Deliberately a string, not a flag: the value an operator
    /// writes is the decision the runbook documents.
    /// </summary>
    public string? ScanOrigin { get; set; }

    /// <summary>Per-request budget for the passive lookups.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// How many distinct names one subdomain enumeration may record. A CT
    /// mirror can return tens of thousands of rows; the artifact stays
    /// bounded and the excess names stay in the mirror.
    /// </summary>
    public int MaxSubdomainNames { get; set; } = 5_000;

    /// <summary>Per-port connect budget for the scan.</summary>
    public int ScanConnectTimeoutMilliseconds { get; set; } = 1_500;

    /// <summary>How many ports the scan may probe concurrently.</summary>
    public int ScanConcurrency { get; set; } = 128;

    /// <summary>
    /// How many ports one scan request may name. The cap that keeps one
    /// request bounded; a full-range sweep is several requests.
    /// </summary>
    public int ScanMaxPorts { get; set; } = 4_096;

    /// <summary>True when the RDAP half has a service to ride.</summary>
    public bool RdapConfigured => !string.IsNullOrWhiteSpace(RdapBaseUrl);

    /// <summary>True when the subdomain half has a mirror to ride.</summary>
    public bool CtConfigured => !string.IsNullOrWhiteSpace(CtBaseUrl);

    /// <summary>
    /// True when the scan's origin has been deliberately named and is one
    /// this teamserver ships. A named-but-unknown origin stays unarmed --
    /// the runbook, not a guess, decides what an unrecognized value means.
    /// </summary>
    public bool ScanConfigured =>
        string.Equals(ScanOrigin?.Trim(), TeamserverOrigin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the origin is named but not one this teamserver ships:
    /// the route answers 503 naming the runbook instead of silently
    /// scanning from a place nobody chose.
    /// </summary>
    public bool ScanOriginUnsupported =>
        !string.IsNullOrWhiteSpace(ScanOrigin) && !ScanConfigured;
}
