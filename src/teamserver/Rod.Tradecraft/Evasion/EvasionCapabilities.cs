using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Evasion;

/// <summary>
/// The evasion capability verbs (architecture.md Sec 10.1, the "evasion"
/// category): detection-evasion hooks within an authorized engagement. These are
/// the verbs  loads through the registry alongside the core, recon,
/// lateral, persist, collect, and exfil sets, so the registry lists them and a
/// future task-issuance path can resolve them.
/// </summary>
/// <remarks>
/// Evasion is a sensitive category (architecture.md Sec 10.2, Sec 13,
/// RESPONSIBLE-USE.md, AGENTS.md Sec 7): the core repository defines the
/// contract -- the interfaces, registration, dispatch, and data shapes -- and
/// supplies no concrete bypass techniques, weaponized code, or in-the-wild
/// proof-of-concepts. Concrete evasion behavior lives in separate, opt-in,
/// out-of-tree modules that register for one of these verbs. The reference
/// implants ship no evasion (architecture.md Sec 5). Here they are descriptors
/// only -- enough for the registry to know each verb exists. Each verb carries a
/// <c>modifies-defenses</c> OPSEC attribute because it alters the target's
/// defensive or monitoring posture (architecture.md Sec 7), so operators and
/// tradecraft filters can surface or suppress it.
/// <para>
/// Unlike the recon, lateral, persist, collect, and exfil verbs, evasion verbs
/// are <b>not</b> gated to a class in <c>ImplantClassCapabilities</c>
/// (architecture.md Sec 5.2, Sec 10.1): the docs mark evasion as contract and
/// dispatch only. Which class an evasion module runs on is decided when an
/// operator deploys the out-of-tree module -- that decision belongs to the live
/// task path, not to this contract milestone.
/// </para>
/// </remarks>
public static class EvasionCapabilities
{
    /// <summary>Take evasive action against a detection on the target.</summary>
    public const string Avoid = "evasion.avoid";

    /// <summary>Unload or remove a defensive component from the target.</summary>
    public const string Unload = "evasion.unload";

    // The OPSEC attribute flagging an evasion verb as altering the target's
    // defensive posture, so a future tradecraft filter can surface or suppress it
    // (architecture.md Sec 7).
    private static readonly IReadOnlyDictionary<string, string> ModifiesDefenses =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["modifies-defenses"] = "true",
        };

    /// <summary>
    /// Descriptors for every evasion verb, in declared order. The composition
    /// root registers these so the registry lists the full evasion set.
    /// </summary>
    public static readonly CapabilityDescriptor[] All =
    {
        CapabilityDescriptor.Of(Avoid, CapabilityCategory.Evasion, "1.0", ModifiesDefenses),
        CapabilityDescriptor.Of(Unload, CapabilityCategory.Evasion, "1.0", ModifiesDefenses),
    };

    /// <summary>Every evasion verb string, in declared order.</summary>
    public static readonly string[] Verbs =
    {
        Avoid, Unload,
    };
}
