using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Exfil;

/// <summary>
/// The exfiltration capability verbs (architecture.md Sec 10.1, the "exfil"
/// category): staging collected data and transferring it over the C2 channel
/// within an authorized engagement. These are the verbs roadmap M5.4 loads
/// through the registry alongside the core, recon, lateral, persist, and collect
/// sets, so the registry lists them and a future task-issuance path can resolve
/// them.
/// </summary>
/// <remarks>
/// Concrete exfiltration behavior is not part of this repository (architecture.md
/// Sec 13, AGENTS.md Sec 7): it lives in the implant or arrives as an out-of-tree
/// module that registers for one of these verbs. The reference implants ship no
/// exfiltration (architecture.md Sec 5, RESPONSIBLE-USE.md). Here they are
/// descriptors only -- enough for the registry to know each verb exists.
/// <see cref="Push"/> carries a <c>touches-network</c> OPSEC attribute because it
/// transfers data over the C2 channel (architecture.md Sec 7), like the
/// network-touching recon and lateral verbs; <see cref="Stage"/> stages
/// already-collected data on the teamserver and touches neither the target's
/// network nor its disk, so it carries no such flag, like the read-only
/// <c>persist.list</c> and the host-local <c>recon.hostenum</c>.
/// </remarks>
public static class ExfilCapabilities
{
    /// <summary>Push collected data out over the C2 channel.</summary>
    public const string Push = "exfil.push";

    /// <summary>Stage collected data on the teamserver, scoped to the engagement.</summary>
    public const string Stage = "exfil.stage";

    // The OPSEC attribute flagging the network-touching exfil verb, so a future
    // tradecraft filter can surface or suppress it (architecture.md Sec 7).
    private static readonly IReadOnlyDictionary<string, string> TouchesNetwork =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["touches-network"] = "true",
        };

    /// <summary>
    /// Descriptors for every exfil verb, in declared order. The composition root
    /// registers these so the registry lists the full exfil set.
    /// </summary>
    public static readonly CapabilityDescriptor[] All =
    {
        CapabilityDescriptor.Of(Push, CapabilityCategory.Exfil, "1.0", TouchesNetwork),
        CapabilityDescriptor.Of(Stage, CapabilityCategory.Exfil, "1.0"),
    };

    /// <summary>Every exfil verb string, in declared order.</summary>
    public static readonly string[] Verbs =
    {
        Push, Stage,
    };
}
