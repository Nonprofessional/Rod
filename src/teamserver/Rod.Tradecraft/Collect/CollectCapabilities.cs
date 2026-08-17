using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Collect;

/// <summary>
/// The collection capability verbs (architecture.md Sec 10.1, the "collect"
/// category): credential and input collection within an authorized engagement.
/// File transfer is a core verb, not collection -- operator file movement lives
/// under <c>file.push</c>/<c>file.pull</c>.
/// </summary>
/// <remarks>
/// Each verb carries an OPSEC attribute flagging what it reads so operators and
/// tradecraft filters can surface or suppress it (architecture.md Sec 7):
/// <see cref="Cred"/> reads a credential, and <see cref="Keylog"/> installs a
/// resident input-capture hook (so it both reads input and persists on the
/// target). Concrete behavior lives on the implant or arrives as an out-of-tree
/// module that registers for a verb (architecture.md Sec 10.2/13): the
/// reference implant implements the credential-store listings, and keylog stays
/// contract-only.
/// </remarks>
public static class CollectCapabilities
{
    /// <summary>Enumerate the target's standard credential stores.</summary>
    public const string Cred = "collect.cred";

    /// <summary>Capture input from the target (keystrokes). Contract-only.</summary>
    public const string Keylog = "collect.keylog";

    // The OPSEC attributes flagging what each collect verb reads, so a
    // tradecraft filter can surface or suppress it (architecture.md Sec 7).
    private static readonly IReadOnlyDictionary<string, string> ReadsCredential =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["reads-credential"] = "true",
        };

    private static readonly IReadOnlyDictionary<string, string> ReadsInputAndPersists =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["reads-input"] = "true",
            ["persists"] = "true",
        };

    /// <summary>
    /// Descriptors for every collect verb, in declared order. The composition
    /// root registers these so the registry lists the full collect set.
    /// </summary>
    public static readonly CapabilityDescriptor[] All =
    {
        CapabilityDescriptor.Of(Cred, CapabilityCategory.Collect, "1.0", ReadsCredential),
        CapabilityDescriptor.Of(Keylog, CapabilityCategory.Collect, "1.0", ReadsInputAndPersists),
    };

    /// <summary>Every collect verb string, in declared order.</summary>
    public static readonly string[] Verbs =
    {
        Cred, Keylog,
    };
}
