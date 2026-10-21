
namespace Rod.Capabilities.Core;

/// <summary>
/// The core capability verbs (architecture.md Sec 10.1, the "core" category):
/// the mandatory-to-useful baseline every implant is expected to carry --
/// command execution, file transfer in both directions, and process
/// termination. These
/// are the verbs the registry loads through the capabilities layer's contract.
/// </summary>
/// <remarks>
/// Concrete behavior for these verbs is not part of this repository -- it lives
/// in the implant and is captured as task output over the beacon stream
/// (architecture.md Sec 10.3). Here they are descriptors only: enough for the
/// registry to know the verb exists and for the task-issuance gate to resolve
/// it. The file verbs carry OPSEC attributes (architecture.md Sec 7) so the
/// picker can badge them: a push writes to the target's disk, a pull reads its
/// filesystem; <see cref="ProcKill"/> ends a process the target was running,
/// an observable and irreversible action an operator should take deliberately.
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

    /// <summary>
    /// List one directory of the target: names, entry kinds, sizes, and
    /// modification times, one JSON object per output line. The browsing
    /// companion of the transfer pair -- an operator file browser walks the
    /// target's tree with it before pushing or pulling.
    /// </summary>
    public const string FsList = "fs.list";

    /// <summary>Terminate one process on the target by its pid.</summary>
    public const string ProcKill = "proc.kill";

    /// <summary>
    /// Retune the live contact cadence: the base sleep and its jitter,
    /// applied from the target's next cycle. The runtime twin of the baked
    /// contact profile -- an operator can speed a fielded implant up for
    /// live work (<c>0 0</c> contacts back-to-back, the interactive-as-poll
    /// posture) or slow it down for long-haul quiet, with no rebuild.
    /// </summary>
    public const string BeaconSleep = "beacon.sleep";

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

    // The OPSEC attribute flagging the terminate verb: killing a pid is an
    // observable, irreversible action on the target (architecture.md Sec 7).
    private static readonly IReadOnlyDictionary<string, string> KillsProcess =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["kills-process"] = "true",
        };

    /// <summary>
    /// Descriptors for every core verb, in declared order. The composition root
    /// registers these so the registry lists the full core set. Each carries
    /// its parameter schema (the task form the operator UI renders generically)
    /// and, where its results speak a documented grammar, its output kind
    /// (architecture.md Sec 15.2) -- the same declaration a module's descriptor
    /// makes, because the built-ins ride the identical contract.
    /// </summary>
    public static readonly CapabilityDescriptor[] All =
    {
        CapabilityDescriptor.Of(ShellExec, CapabilityCategory.Core, "1.0", parameters: new[]
        {
            new VerbParameter("command", "Command", VerbParameterKind.Text, Required: true,
                Placeholder: "whoami", Trailing: true),
        }),
        CapabilityDescriptor.Of(ShellInteract, CapabilityCategory.Core, "1.0"),
        CapabilityDescriptor.Of(FilePush, CapabilityCategory.Core, "1.0", WritesToDisk, new[]
        {
            new VerbParameter("file", "Local file", VerbParameterKind.File, Required: true),
            new VerbParameter("path", "Remote path", VerbParameterKind.String, Required: true,
                Placeholder: "/tmp/tool.exe",
                Help: "The remote absolute path the bytes land at. Larger uploads stage server-side and stream to the implant."),
        }),
        CapabilityDescriptor.Of(FilePull, CapabilityCategory.Core, "1.0", ReadsFilesystem,
        parameters: new[]
        {
            new VerbParameter("path", "Remote path", VerbParameterKind.String, Required: true,
                Placeholder: "/etc/hostname",
                Help: "Small files return inline in the task output; larger ones stream back as a downloadable artifact."),
        },
        output: new VerbOutput(VerbOutputKind.File)),
        CapabilityDescriptor.Of(FsList, CapabilityCategory.Core, "1.0", ReadsFilesystem,
        parameters:
        [
            new VerbParameter("path", "Directory", VerbParameterKind.String, Required: true,
                Placeholder: "/",
                Help: "Names, entry kinds, sizes, and modification times, one JSON entry per line."),
        ],
        output: new VerbOutput(VerbOutputKind.FileList)),
        CapabilityDescriptor.Of(ProcKill, CapabilityCategory.Core, "1.0", KillsProcess, new[]
        {
            new VerbParameter("pid", "PID", VerbParameterKind.Number, Required: true, Placeholder: "4242"),
        }),
        CapabilityDescriptor.Of(BeaconSleep, CapabilityCategory.Core, "1.0", parameters: new[]
        {
            new VerbParameter("sleep", "Contact every", VerbParameterKind.String, Required: true,
                Placeholder: "10s, 5m, or bare seconds (0 = back-to-back)",
                Help: "Go duration (30s, 5m) or bare seconds. 0 contacts continuously -- the interactive-as-poll posture."),
            new VerbParameter("jitter", "Randomize +/-", VerbParameterKind.String,
                Placeholder: "2s -- empty keeps the current jitter"),
        }),
    };

    /// <summary>Every core verb string, in declared order.</summary>
    public static readonly string[] Verbs =
    {
        ShellExec, ShellInteract, FilePush, FilePull, FsList, ProcKill, BeaconSleep,
    };
}
