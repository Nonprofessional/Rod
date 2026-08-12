using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Collect;

/// <summary>
/// The collection capability verbs (architecture.md Sec 10.1, the "collect"
/// category): file, credential, and input collection within an authorized
/// engagement. These are the verbs roadmap M5.4 loads through the registry
/// alongside the core, recon, lateral, and persist sets, so the registry lists
/// them and a future task-issuance path can resolve them.
/// </summary>
/// <remarks>
/// Concrete collection behavior is not part of this repository (architecture.md
/// Sec 13, AGENTS.md Sec 7): it lives in the implant or arrives as an out-of-tree
/// module that registers for one of these verbs. The reference implants ship no
/// collection (architecture.md Sec 5, RESPONSIBLE-USE.md). Here they are
/// descriptors only -- enough for the registry to know each verb exists. Each
/// verb carries an OPSEC attribute flagging what it reads so operators and
/// tradecraft filters can surface or suppress it (architecture.md Sec 7):
/// <see cref="File"/> reads the filesystem, <see cref="Cred"/> reads a
/// credential, and <see cref="Keylog"/> installs a resident input-capture hook
/// (so it both reads input and persists on the target).
/// </remarks>
public static class CollectCapabilities
{
    /// <summary>Collect a file from the target.</summary>
    public const string File = "collect.file";

    /// <summary>Collect a credential from the target.</summary>
    public const string Cred = "collect.cred";

    /// <summary>Capture input from the target (e.g. keystrokes).</summary>
    public const string Keylog = "collect.keylog";

    // The OPSEC attributes flagging what each collect verb reads, so a future
    // tradecraft filter can surface or suppress it (architecture.md Sec 7).
    private static readonly IReadOnlyDictionary<string, string> ReadsFilesystem =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["reads-filesystem"] = "true",
        };

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
        CapabilityDescriptor.Of(File, CapabilityCategory.Collect, "1.0", ReadsFilesystem),
        CapabilityDescriptor.Of(Cred, CapabilityCategory.Collect, "1.0", ReadsCredential),
        CapabilityDescriptor.Of(Keylog, CapabilityCategory.Collect, "1.0", ReadsInputAndPersists),
    };

    /// <summary>Every collect verb string, in declared order.</summary>
    public static readonly string[] Verbs =
    {
        File, Cred, Keylog,
    };
}
