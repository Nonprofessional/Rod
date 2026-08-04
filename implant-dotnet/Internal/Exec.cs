using System.Diagnostics;
using Rod.V1;

namespace Rod.Implant.Internal;

// Dispatches the core capability verbs the reference implant advertises
// (architecture.md Sec 10). Only shell.exec is wired in this milestone; the
// runner is the dispatch point future verbs (file.push, probe.read, ...) extend.
//
// This is a benign reference runner: it shells out to the platform shell for the
// one core verb and reports output. It performs no evasion, no obfuscation, and
// no destructive behavior (RESPONSIBLE-USE.md, architecture.md Sec 7).

/// <summary>
/// Dispatches capability verbs. Safe for concurrent use: each Dispatch call runs
/// an independent process.
/// </summary>
internal sealed class Runner
{
    /// <summary>
    /// Runs <paramref name="verb"/> against <paramref name="arguments"/> and
    /// returns the wire outcome plus the captured output (combined
    /// stdout/stderr). An unknown verb reports Failed with a clear message rather
    /// than throwing, so the operator sees the cause.
    /// </summary>
    public (TaskOutcome Outcome, string Output) Dispatch(string verb, string arguments)
    {
        return verb switch
        {
            "shell.exec" => ShellExec(arguments),
            _ => (TaskOutcome.Failed, "unknown verb: " + verb),
        };
    }

    // Runs the argument string through the platform shell and returns the combined
    // output. A non-zero exit is a Failed outcome with the output captured so the
    // operator sees the cause; the shell itself failing to start is also Failed.
    private static (TaskOutcome, string) ShellExec(string command)
    {
        var (shell, flag) = PlatformShell();
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(flag);
        psi.ArgumentList.Add(command);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return (TaskOutcome.Failed, "failed to start shell");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            var output = ComposeOutput(stdout, stderr);
            if (process.ExitCode != 0)
                return (TaskOutcome.Failed, output.Length > 0 ? output : $"exit code {process.ExitCode}");
            return (TaskOutcome.Succeeded, output);
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, ex.Message);
        }
    }

    // The shell and its command flag for shell.exec on the current OS. Linux/macOS
    // use sh -c; Windows uses cmd /c -- the same split as the Go implant's
    // platformShell.
    private static (string Shell, string Flag) PlatformShell()
        => OperatingSystem.IsWindows() ? ("cmd.exe", "/c") : ("sh", "-c");

    // Joins stdout and stderr on a newline so a Failed outcome shows both, and a
    // Succeeded outcome carries whatever the shell printed.
    private static string ComposeOutput(string stdout, string stderr)
    {
        if (stdout.Length == 0)
            return stderr;
        if (stderr.Length == 0)
            return stdout;
        return stdout + "\n" + stderr;
    }
}
