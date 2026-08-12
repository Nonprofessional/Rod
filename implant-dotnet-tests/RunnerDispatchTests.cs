using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// RunnerDispatchTests ports the runner_test.go dispatch tests from the Go
// reference implant to xUnit: shell.exec success and non-zero-exit failure, and
// the unknown-verb refusal. Each drives Runner.Dispatch against the real
// platform shell, the same loopback-free counterpart to ReconTests.
public class RunnerDispatchTests
{
    [Fact]
    public void ShellExec_Succeeds()
    {
        var runner = new Runner();
        var (outcome, output, _) = runner.Dispatch("shell.exec", "echo hello-rod");
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("hello-rod", output);
    }

    [Fact]
    public void ShellExec_FailedOutcome_OnBadCommand()
    {
        // sh cannot exec a missing binary (exit 127); cmd prints an error and
        // exits 1. Either way the runner reports Failed.
        var runner = new Runner();
        var (outcome, _, _) = runner.Dispatch("shell.exec", "this-command-does-not-exist-xyz");
        Assert.Equal(TaskOutcome.Failed, outcome);
    }

    [Fact]
    public void UnknownVerb_FailedWithCause()
    {
        var runner = new Runner();
        var (outcome, output, _) = runner.Dispatch("file.push", "/tmp/x");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("file.push", output);
    }
}
