using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Persist;

/// <summary>
/// The persistence capability verbs (architecture.md Sec 10.1, the "persist"
/// category): establishing, enumerating, and tearing down footholds within an
/// authorized engagement. These are the verbs  loads through the
/// registry alongside the core, recon, and lateral sets, so the registry lists
/// them and a future task-issuance path can resolve them.
/// </summary>
/// <remarks>
/// Concrete persistence behavior is not part of this repository (architecture.md
/// Sec 13, AGENTS.md Sec 7): it lives in the implant or arrives as an out-of-tree
/// module that registers for one of these verbs. The reference implants ship no
/// persistence (architecture.md Sec 5, RESPONSIBLE-USE.md). Here they are
/// descriptors only -- enough for the registry to know each verb exists. Each
/// state-changing verb carries OPSEC attributes so operators and tradecraft
/// filters can surface or suppress it (architecture.md Sec 7): <see cref="Install"/>
/// writes to disk and establishes a foothold, and <see cref="Remove"/> writes to
/// disk; <see cref="List"/> is a read and carries no such flag, like the
/// host-local <c>recon.hostenum</c>.
/// </remarks>
public static class PersistCapabilities
{
    /// <summary>Install a persistence mechanism on the target.</summary>
    public const string Install = "persist.install";

    /// <summary>Remove a previously-installed persistence mechanism.</summary>
    public const string Remove = "persist.remove";

    /// <summary>Enumerate the persistence mechanisms installed on the target.</summary>
    public const string List = "persist.list";

    // The OPSEC attributes flagging what each persist verb touches, so a future
    // tradecraft filter can surface or suppress it (architecture.md Sec 7).
    private static readonly IReadOnlyDictionary<string, string> WritesDiskAndPersists =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["writes-to-disk"] = "true",
            ["persists"] = "true",
        };

    private static readonly IReadOnlyDictionary<string, string> WritesToDisk =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["writes-to-disk"] = "true",
        };

    /// <summary>
    /// Descriptors for every persist verb, in declared order. The composition
    /// root registers these so the registry lists the full persist set.
    /// </summary>
    public static readonly CapabilityDescriptor[] All =
    {
        CapabilityDescriptor.Of(Install, CapabilityCategory.Persist, "1.0", WritesDiskAndPersists),
        CapabilityDescriptor.Of(Remove, CapabilityCategory.Persist, "1.0", WritesToDisk),
        CapabilityDescriptor.Of(List, CapabilityCategory.Persist, "1.0"),
    };

    /// <summary>Every persist verb string, in declared order.</summary>
    public static readonly string[] Verbs =
    {
        Install, Remove, List,
    };
}
