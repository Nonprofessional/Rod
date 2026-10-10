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
    /// <c>file.push</c>, <c>file.pull</c>).
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
    /// Network tunneling through an implant (architecture.md Sec 5.2, Sec 14):
    /// bridging operator traffic to hosts reachable only from the implant's
    /// vantage. The tunnel verbs run as live channels (architecture.md Sec
    /// 10.3); the pivot class admits exactly this category.
    /// </summary>
    Tunnel,

    /// <summary>
    /// The implant-side plugin seam's own verbs (architecture.md Sec 5.4):
    /// <c>module.load</c>, <c>module.unload</c>, and <c>module.list</c> --
    /// the machinery that widens an implanted artifact's runtime verb set
    /// with C-ABI capability modules delivered over the task channel. The
    /// verbs they load arrive through the categories above (recon, lateral,
    /// persist, collect, exfil) or a paired out-of-tree descriptor; this
    /// category holds the loading machinery itself, and no other class
    /// carries it (module support is the Implant class's, Sec 5.2).
    /// </summary>
    Module,

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

    /// <summary>
    /// The Browser class's hook verbs (architecture.md Sec 5.2, Sec 10.1):
    /// the read-and-steer set a hooked page justifies. The concrete handlers
    /// run inside the served hook script, not on a compiled implant; what
    /// sits past the set -- input capture, browser-exploit chaining -- is an
    /// out-of-tree capability contract, not a category here (Sec 13).
    /// </summary>
    Browser,
}
