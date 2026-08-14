using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Recon;

/// <summary>
/// The recon capability verbs (architecture.md Sec 10.1, the "recon" category):
/// target and network reconnaissance -- port scanning, host enumeration, and
/// service probing. These are the verbs  loads through the registry
/// alongside the core set, so the registry lists them and a future task-issuance
/// path can resolve them.
/// </summary>
/// <remarks>
/// Concrete recon behavior is not part of this repository (architecture.md
/// Sec 13, AGENTS.md Sec 7): it lives in the implant and is captured as task
/// output over the beacon stream (architecture.md Sec 10.3), or arrives as an
/// out-of-tree module that registers for one of these verbs. Here they are
/// descriptors only -- enough for the registry to know each verb exists. Each
/// network-touching verb carries a <c>touches-network</c> OPSEC attribute so
/// operators and tradecraft filters can avoid risky actions (architecture.md
/// Sec 7); <see cref="HostEnum"/> introspects the local host and does not touch
/// the network, so it carries no such flag.
/// </remarks>
public static class ReconCapabilities
{
    /// <summary>Scan a host for open TCP ports.</summary>
    public const string PortScan = "recon.portscan";

    /// <summary>Enumerate facts about a host (the local host by default).</summary>
    public const string HostEnum = "recon.hostenum";

    /// <summary>Probe one or more ports for a service banner.</summary>
    public const string Service = "recon.service";

    // The OPSEC attribute flagging a verb as touching the network, so a future
    // tradecraft filter can surface or suppress it (architecture.md Sec 7).
    private static readonly IReadOnlyDictionary<string, string> TouchesNetwork =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["touches-network"] = "true",
        };

    /// <summary>
    /// Descriptors for every recon verb, in declared order. The composition root
    /// registers these so the registry lists the full recon set.
    /// </summary>
    public static readonly CapabilityDescriptor[] All =
    {
        CapabilityDescriptor.Of(PortScan, CapabilityCategory.Recon, "1.0", TouchesNetwork),
        CapabilityDescriptor.Of(HostEnum, CapabilityCategory.Recon, "1.0"),
        CapabilityDescriptor.Of(Service, CapabilityCategory.Recon, "1.0", TouchesNetwork),
    };

    /// <summary>Every recon verb string, in declared order.</summary>
    public static readonly string[] Verbs =
    {
        PortScan, HostEnum, Service,
    };
}
