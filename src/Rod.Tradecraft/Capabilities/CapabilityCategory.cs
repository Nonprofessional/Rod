namespace Rod.Tradecraft.Capabilities;

/// <summary>
/// The category a <see cref="CapabilityDescriptor"/> belongs to
/// (architecture.md Sec 10.1). Categories group verbs by operational purpose
/// (core, recon, lateral movement, persistence, collection, exfiltration) and
/// mark the two sensitive categories -- <see cref="Evasion"/> and
/// <see cref="Exploit"/> -- that this repository defines only as pluggable
/// contracts (architecture.md Sec 13, AGENTS.md Sec 7): their concrete
/// tradecraft is supplied as separate, opt-in, out-of-tree modules.
/// </summary>
public enum CapabilityCategory
{
    /// <summary>
    /// The mandatory-to-useful baseline (<c>shell.exec</c>, <c>file.push</c>,
    /// <c>file.pull</c>, <c>tunnel.open</c>, <c>probe.read</c>).
    /// </summary>
    Core,

    /// <summary>Target and network reconnaissance.</summary>
    Recon,

    /// <summary>Lateral movement within authorized scope.</summary>
    Lateral,

    /// <summary>Persistence mechanisms.</summary>
    Persist,

    /// <summary>Data and credential collection.</summary>
    Collect,

    /// <summary>Exfiltration over the C2 channel.</summary>
    Exfil,

    /// <summary>
    /// Detection-evasion hooks. Contract and dispatch only; concrete behavior
    /// is out-of-tree (architecture.md Sec 13).
    /// </summary>
    Evasion,

    /// <summary>
    /// PoC/exploit integration point. Contract and dispatch only; concrete
    /// behavior is out-of-tree (architecture.md Sec 13).
    /// </summary>
    Exploit,
}
