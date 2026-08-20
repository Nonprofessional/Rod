using System.Diagnostics;
using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// Covers the process verbs (architecture.md Sec 10.1): recon.ps lists the
// local processes over the standard OS process APIs and proc.kill terminates
// one by pid. The listing is asserted against processes the test itself
// started, so the assertions pin the handler's contract (every row carries
// pid, ppid, user, and image) without depending on what else the host runs.
public class ProcTests
{
    private static HandlerRegistry NewRegistry() => HandlerRegistry.Default();

    [Fact]
    public void List_ReportsSelfWithPidPpidUserAndImage()
    {
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("recon.ps", "");
        Assert.Equal(TaskOutcome.Succeeded, outcome);

        // The test host's own process is listed with all four fields; ppid is
        // a live process (>= 1), the user resolves to a name or a numeric uid,
        // and the image is non-empty.
        var row = Assert.Single(
            output.Split('\n'),
            line => line.StartsWith($"pid={Environment.ProcessId} ", StringComparison.Ordinal));
        Assert.Contains("ppid=", row);
        Assert.Contains("user=", row);
        Assert.Contains("image=", row);

        var ppid = int.Parse(Field(row, "ppid="));
        Assert.True(ppid >= 1, $"expected a live parent pid, got {ppid}");
        Assert.False(string.IsNullOrEmpty(Field(row, "user=")));
        Assert.False(string.IsNullOrEmpty(Field(row, "image=")));
    }

    [Fact]
    public void Kill_TerminatesAStartedProcessByPid()
    {
        using var victim = StartSleepProcess();
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("proc.kill", victim.Id.ToString());
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains(victim.Id.ToString(), output);

        victim.WaitForExit(5000);
        Assert.True(victim.HasExited);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-pid")]
    [InlineData("-1")]
    [InlineData("0")]
    public void Kill_MalformedArgs_FailWithCause(string arguments)
    {
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("proc.kill", arguments);
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("proc.kill", output);
    }

    [Fact]
    public void Kill_MissingPid_FailsWithCause()
    {
        // A pid nothing owns: a high nonzero value is virtually never live in a
        // test environment, and the contract under test is the refusal shape,
        // not the specific id.
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("proc.kill", int.MaxValue.ToString());
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("no such process", output);
    }

    // Starts a disposable long-running process to terminate: `sh -c exec sleep`
    // replaces itself with sleep (so the pid is the sleeper), cmd's `timeout`
    // is the Windows stand-in.
    private static Process StartSleepProcess()
        => Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            Arguments = OperatingSystem.IsWindows() ? "/c timeout /t 30 /nobreak" : "-c \"exec sleep 30\"",
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("failed to start the victim process");

    // Extracts the value of a "key=" field from a row of recon.ps output.
    private static string Field(string row, string key)
    {
        var at = row.IndexOf(key, StringComparison.Ordinal);
        Assert.True(at >= 0, $"expected field '{key}' in row '{row}'");
        var rest = row[(at + key.Length)..];
        var end = rest.IndexOf(' ');
        return end < 0 ? rest : rest[..end];
    }
}
