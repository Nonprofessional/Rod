using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Core;

/// <summary>
/// The core capability verbs (architecture.md Sec 10.1, the "core" category):
/// the mandatory-to-useful baseline every implant is expected to carry --
/// command execution and file transfer in both directions. These
/// are the verbs the registry loads through the tradecraft layer's contract.
/// </summary>
/// <remarks>
/// Concrete behavior for these verbs is not part of this repository -- it lives
/// in the implant and is captured as task output over the beacon stream
/// (architecture.md Sec 10.3). Here they are descriptors only: enough for the
/// registry to know the verb exists and for the task-issuance gate to resolve
/// it. The file verbs carry OPSEC attributes (architecture.md Sec 7) so the
/// picker can badge them: a push writes to the target's disk, a pull reads its
/// filesystem.
/// </remarks>
public static class CoreCapabilities
{
    /// <summary>One-shot shell command execution.</summary>
    public const string ShellExec = "shell.exec";

    /// <summary>
    /// The interactive shell: shell.exec's streaming shape
    /// (architecture.md Sec 10.3). The task opens a session-scoped channel;
    /// the operator's input flows down it and the shell's output streams back
    /// until the operator closes stdin or the shell exits.
    /// </summary>
    public const string ShellInteract = "shell.interact";

    /// <summary>Upload a file onto the target.</summary>
    public const string FilePush = "file.push";

    /// <summary>Download a file off the target.</summary>
    public const string FilePull = "file.pull";

    // The OPSEC attributes for the file verbs (architecture.md Sec 7): a push
    // lands bytes on the target's disk, a pull reads its filesystem.
    private static readonly IReadOnlyDictionary<string, string> WritesToDisk =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["writes-to-disk"] = "true",
        };

    private static readonly IReadOnlyDictionary<string, string> ReadsFilesystem =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["reads-filesystem"] = "true",
        };

    /// <summary>
    /// Descriptors for every core verb, in declared order. The composition root
    /// registers these so the registry lists the full core set.
    /// </summary>
    public static readonly CapabilityDescriptor[] All =
    {
        CapabilityDescriptor.Of(ShellExec, CapabilityCategory.Core, "1.0"),
        CapabilityDescriptor.Of(ShellInteract, CapabilityCategory.Core, "1.0"),
        CapabilityDescriptor.Of(FilePush, CapabilityCategory.Core, "1.0", WritesToDisk),
        CapabilityDescriptor.Of(FilePull, CapabilityCategory.Core, "1.0", ReadsFilesystem),
    };

    /// <summary>Every core verb string, in declared order.</summary>
    public static readonly string[] Verbs =
    {
        ShellExec, ShellInteract, FilePush, FilePull,
    };
}
