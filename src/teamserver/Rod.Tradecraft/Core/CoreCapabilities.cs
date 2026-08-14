using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Core;

/// <summary>
/// The core capability verbs (architecture.md Sec 10.1, the "core" category):
/// the mandatory-to-useful baseline every implant is expected to carry. These
/// are the verbs the skeleton loads through the registry to prove core verbs
/// flow through the tradecraft layer's contract ().
/// </summary>
/// <remarks>
/// Concrete behavior for these verbs is not part of this repository -- it lives
/// in the implant and is captured as task output over the beacon stream
/// (architecture.md Sec 10.3). Here they are descriptors only: enough for the
/// registry to know the verb exists and for a future task-issuance gate to
/// resolve it.
/// </remarks>
public static class CoreCapabilities
{
    /// <summary>One-shot shell command execution.</summary>
    public const string ShellExec = "shell.exec";

    /// <summary>Upload a file to the implant.</summary>
    public const string FilePush = "file.push";

    /// <summary>Download a file from the implant.</summary>
    public const string FilePull = "file.pull";

    /// <summary>Open a tunnel through the implant.</summary>
    public const string TunnelOpen = "tunnel.open";

    /// <summary>Read a host/probe value.</summary>
    public const string ProbeRead = "probe.read";

    /// <summary>
    /// Descriptors for every core verb, in declared order. The composition root
    /// registers these so the registry lists the full core set.
    /// </summary>
    public static readonly CapabilityDescriptor[] All =
    {
        CapabilityDescriptor.Of(ShellExec, CapabilityCategory.Core, "1.0"),
        CapabilityDescriptor.Of(FilePush, CapabilityCategory.Core, "1.0"),
        CapabilityDescriptor.Of(FilePull, CapabilityCategory.Core, "1.0"),
        CapabilityDescriptor.Of(TunnelOpen, CapabilityCategory.Core, "1.0"),
        CapabilityDescriptor.Of(ProbeRead, CapabilityCategory.Core, "1.0"),
    };

    /// <summary>Every core verb string, in declared order.</summary>
    public static readonly string[] Verbs =
    {
        ShellExec, FilePush, FilePull, TunnelOpen, ProbeRead,
    };
}
