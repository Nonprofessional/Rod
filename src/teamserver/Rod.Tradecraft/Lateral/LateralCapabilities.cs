using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Lateral;

/// <summary>
/// The lateral-movement capability verbs (architecture.md Sec 10.1, the
/// "lateral" category): movement within authorized scope -- deriving a child
/// implant, reusing a credential token, and executing on a remote host. These
/// are the verbs  loads through the registry alongside the core and
/// recon sets, so the registry lists them and a future task-issuance path can
/// resolve them.
/// </summary>
/// <remarks>
/// Concrete lateral-movement behavior is not part of this repository
/// (architecture.md Sec 13, AGENTS.md Sec 7): it lives in the implant or arrives
/// as an out-of-tree module that registers for one of these verbs. Here they are
/// descriptors only -- enough for the registry to know each verb exists. Each
/// verb carries an OPSEC attribute so operators and tradecraft filters can
/// surface or suppress it (architecture.md Sec 7): <see cref="Move"/> derives a
/// child implant, <see cref="Token"/> handles a credential, and
/// <see cref="ExecRemote"/> touches the network.
/// </remarks>
public static class LateralCapabilities
{
    /// <summary>
    /// Derive a child implant on the target -- the deployment verb whose child
    /// enrols into the same engagement and records its parent (architecture.md
    /// Sec 5.2). The server-side parentage recording is in core state; the
    /// implant-side derivation is out-of-tree.
    /// </summary>
    public const string Move = "lateral.move";

    /// <summary>Reuse a credential token for lateral authentication.</summary>
    public const string Token = "lateral.token";

    /// <summary>Execute a command on a remote host within authorized scope.</summary>
    public const string ExecRemote = "lateral.exec_remote";

    // The OPSEC attributes flagging what each lateral verb touches, so a future
    // tradecraft filter can surface or suppress it (architecture.md Sec 7).
    private static readonly IReadOnlyDictionary<string, string> DerivesChild =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["derives-child"] = "true",
        };

    private static readonly IReadOnlyDictionary<string, string> TouchesCredential =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["touches-credential"] = "true",
        };

    private static readonly IReadOnlyDictionary<string, string> TouchesNetwork =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["touches-network"] = "true",
        };

    /// <summary>
    /// Descriptors for every lateral verb, in declared order. The composition
    /// root registers these so the registry lists the full lateral set.
    /// </summary>
    public static readonly CapabilityDescriptor[] All =
    {
        CapabilityDescriptor.Of(Move, CapabilityCategory.Lateral, "1.0", DerivesChild),
        CapabilityDescriptor.Of(Token, CapabilityCategory.Lateral, "1.0", TouchesCredential),
        CapabilityDescriptor.Of(ExecRemote, CapabilityCategory.Lateral, "1.0", TouchesNetwork),
    };

    /// <summary>Every lateral verb string, in declared order.</summary>
    public static readonly string[] Verbs =
    {
        Move, Token, ExecRemote,
    };
}
