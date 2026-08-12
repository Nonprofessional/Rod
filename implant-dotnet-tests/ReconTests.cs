using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// ReconTests ports recon_test.go from the Go reference implant to xUnit,
// covering the recon verbs added in roadmap M5.1 (recon.portscan,
// recon.hostenum, recon.service). Each network-touching test drives
// Runner.Dispatch against a real loopback listener so an open port is observable
// without a network dependency, mirroring how RunnerDispatchTests exercises
// shell.exec against the real platform shell.
public class ReconTests
{
    private static Runner NewRunner() => new();

    // Builds "<host> <start-end>" over a tight window around port so the scan
    // finishes promptly while still covering the open listener.
    private static string ScanArgs(int port)
    {
        var start = Math.Max(1, port - 1);
        return $"127.0.0.1 {start}-{port + 1}";
    }

    [Fact]
    public void PortScan_ReportsOpenLoopbackPort()
    {
        using var ln = LoopbackListener.Start();
        var runner = NewRunner();
        var (outcome, output, _) = runner.Dispatch("recon.portscan", ScanArgs(ln.Port));
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains($"127.0.0.1:{ln.Port} open", output);
    }

    [Fact]
    public void PortScan_MalformedArgs_FailsWithCause()
    {
        var runner = NewRunner();
        var (outcome, output, _) = runner.Dispatch("recon.portscan", "not-a-range");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("recon.portscan", output);
    }

    [Fact]
    public void PortScan_EmptyRange_SucceedsWithNoLines()
    {
        // A range with no open ports is still a successful scan; the operator
        // sees empty output rather than a failure, so a quiet host is not
        // confused with an error.
        var runner = NewRunner();
        var (outcome, output, _) = runner.Dispatch("recon.portscan", "127.0.0.1 1-1");
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Equal("", output);
    }

    [Fact]
    public void HostEnum_ReportsLocalFacts()
    {
        var runner = NewRunner();
        var (outcome, output, _) = runner.Dispatch("recon.hostenum", "");
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        // hostenum is local introspection; it surfaces the hostname and the
        // os/arch the runner documents. (.NET reports os=/arch= labels, the
        // equivalent of the Go implant's goos=/goarch=.)
        Assert.Contains("hostname=", output);
        Assert.Contains("os=", output);
        Assert.Contains("arch=", output);
    }

    [Fact]
    public void ServiceProbe_ReportsOpenLoopbackPort()
    {
        using var ln = LoopbackListener.Start();
        var runner = NewRunner();
        var (outcome, output, _) = runner.Dispatch("recon.service", $"127.0.0.1 {ln.Port}");
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains($"127.0.0.1:{ln.Port} open", output);
    }

    [Fact]
    public void ServiceProbe_NoOpenPort_Fails()
    {
        // The documented contract: if none of the listed ports is open, the
        // probe reports FAILED. Port 9 is the discard service and almost never
        // bound in a test environment; the assertion is on the contract, not on
        // port 9 being definitively closed.
        var runner = NewRunner();
        var (outcome, output, _) = runner.Dispatch("recon.service", "127.0.0.1 9");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("127.0.0.1", output);
    }

    [Fact]
    public void ServiceProbe_MalformedArgs_FailsWithCause()
    {
        var runner = NewRunner();
        var (outcome, output, _) = runner.Dispatch("recon.service", "127.0.0.1");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("recon.service", output);
    }
}
