using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// RegistryDispatchTests ports the runner_test.go dispatch tests from the Go
// reference implant to xUnit: shell.exec success and non-zero-exit failure, and
// the unknown-verb refusal. Each drives HandlerRegistry.Dispatch against the
// real platform shell, the same loopback-free counterpart to ReconTests.
public class RegistryDispatchTests
{
    [Fact]
    public void ShellExec_Succeeds()
    {
        var registry = HandlerRegistry.Default(enroll: null);
        var (outcome, output, _) = registry.Dispatch("shell.exec", "echo hello-rod");
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("hello-rod", output);
    }

    [Fact]
    public void ShellExec_FailedOutcome_OnBadCommand()
    {
        // sh cannot exec a missing binary (exit 127); cmd prints an error and
        // exits 1. Either way the registry reports Failed.
        var registry = HandlerRegistry.Default(enroll: null);
        var (outcome, _, _) = registry.Dispatch("shell.exec", "this-command-does-not-exist-xyz");
        Assert.Equal(TaskOutcome.Failed, outcome);
    }

    [Fact]
    public void UnknownVerb_FailedWithCause()
    {
        // file.push is in the stage-2 class verb set but ships no compiled
        // handler in the reference implant, so a dispatched file.push reports
        // the cause instead of throwing (architecture.md Sec 5.2/5.3).
        var registry = HandlerRegistry.Default(enroll: null);
        var (outcome, output, _) = registry.Dispatch("file.push", "/tmp/x");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("file.push", output);
    }
}
