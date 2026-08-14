using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// PersistTests ports persist_test.go from the Go reference implant to xUnit,
// covering the persist.* dispatch surface that does not need a privileged
// install: argument parsing, mechanism routing, the persist.list platform
// branch, and a systemd install/list/remove round-trip against a temporary
// XDG_CONFIG_HOME so the test never touches the developer's own units. The
// Windows-only mechanisms (runkey/schtasks/service) are exercised by the
// platform refusal off-Windows and by the parser tests.
public class PersistTests
{
    private static HandlerRegistry NewRegistry() => HandlerRegistry.Default();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("runkey")]
    [InlineData("runkey onlyname")]
    public void PersistInstall_MalformedArgs_FailsWithCause(string args)
    {
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("persist.install", args);
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("persist.install expects", output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cron")]
    [InlineData("cron one two")]
    public void PersistRemove_MalformedArgs_FailsWithCause(string args)
    {
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("persist.remove", args);
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("persist.remove expects", output);
    }

    [Fact]
    public void PersistInstall_UnknownMechanism_FailsWithCause()
    {
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("persist.install", "voodoo name payload");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("unknown mechanism", output);
    }

    [Fact]
    public void PersistList_UnknownMechanism_FailsWithCause()
    {
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("persist.list", "voodoo");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("unknown mechanism", output);
    }

    [Fact]
    public void PersistInstall_WindowsMechanism_NonWindows_RefusesWithCause()
    {
        // Documents the platform contract: the Windows mechanisms refuse
        // off-Windows with a clear cause rather than reaching for tools that do
        // not exist. Asserted on non-Windows hosts (the CI runs Linux).
        if (OperatingSystem.IsWindows()) return;

        var registry = NewRegistry();
        foreach (var mech in new[] { "runkey", "schtasks", "service" })
        {
            var (outcome, output, _) = registry.Dispatch("persist.install", $"{mech} name payload");
            Assert.Equal(TaskOutcome.Failed, outcome);
            Assert.Contains("Windows-only", output);
        }
    }

    [Fact]
    public void PersistList_SucceedsWithMarkerLines()
    {
        if (OperatingSystem.IsWindows()) return; // Linux-only listing fixture

        using var xdg = TempDir.Create();
        using (new EnvScope("XDG_CONFIG_HOME", xdg.Path))
        {
            Directory.CreateDirectory(Path.Combine(xdg.Path, "systemd", "user"));
            File.WriteAllText(Path.Combine(xdg.Path, "systemd", "user", "RodMarker.service"),
                "[Service]\nExecStart=/bin/true\n");

            var registry = NewRegistry();
            var (outcome, output, _) = registry.Dispatch("persist.list", "");
            Assert.Equal(TaskOutcome.Succeeded, outcome);
            Assert.Contains("systemd RodMarker", output);
        }
    }

    [Fact]
    public void PersistInstallSystemd_Remove_RoundTrips()
    {
        if (OperatingSystem.IsWindows()) return; // systemd round-trip runs on Linux only

        using var xdg = TempDir.Create();
        using (new EnvScope("XDG_CONFIG_HOME", xdg.Path))
        {
            var registry = NewRegistry();

            // install: writes the unit file, then daemon-reloads. The .NET
            // handler writes the file before daemon-reload, so tolerate a
            // daemon-reload failure (hosts without systemd) and keep going --
            // the listing reads the directory directly.
            var (ioc, iout, _) = registry.Dispatch("persist.install", "systemd RodRT /bin/true");
            if (ioc == TaskOutcome.Failed && !iout.Contains("daemon-reload"))
                Assert.Fail($"install failed unexpectedly: {iout}");

            // list: the just-installed unit shows up by name.
            var (loc, listing, _) = registry.Dispatch("persist.list", "systemd");
            Assert.Equal(TaskOutcome.Succeeded, loc);
            Assert.Contains("RodRT", listing);

            // remove: deletes the unit file and reloads.
            var (roc, _, _) = registry.Dispatch("persist.remove", "systemd RodRT");
            Assert.Equal(TaskOutcome.Succeeded, roc);

            // list again: the name is gone.
            var (loc2, listing2, _) = registry.Dispatch("persist.list", "systemd");
            Assert.Equal(TaskOutcome.Succeeded, loc2);
            Assert.DoesNotContain("RodRT", listing2);
        }
    }

    [Fact]
    public void PersistRemove_AlreadyAbsent_IdempotentSucceeded()
    {
        if (OperatingSystem.IsWindows()) return; // Linux-only listing fixture

        using var xdg = TempDir.Create();
        using (new EnvScope("XDG_CONFIG_HOME", xdg.Path))
        {
            var registry = NewRegistry();
            var (outcome, output, _) = registry.Dispatch("persist.remove", "systemd NeverInstalled");
            Assert.Equal(TaskOutcome.Succeeded, outcome);
            Assert.Contains("already absent", output);
        }
    }

    [Theory]
    [InlineData("", "", "", "", false)]
    [InlineData("   ", "", "", "", false)]
    [InlineData("runkey", "", "", "", false)]
    [InlineData("runkey name", "", "", "", false)]
    [InlineData("runkey name payload", "runkey", "name", "payload", true)]
    [InlineData("  runkey   RodRun   /bin/true  ", "runkey", "RodRun", "/bin/true", true)]
    [InlineData("cron hourlyjob /usr/bin/backup --quiet", "cron", "hourlyjob", "/usr/bin/backup --quiet", true)]
    public void TryParseInstallArgs_Routes_Fields(
        string input, string mech, string name, string payload, bool ok)
    {
        var result = Persist.TryParseInstallArgs(input, out var m, out var n, out var p);
        Assert.Equal(ok, result);
        Assert.Equal(mech, m);
        Assert.Equal(name, n);
        Assert.Equal(payload, p);
    }

    [Theory]
    [InlineData("", "", "", false)]
    [InlineData("   ", "", "", false)]
    [InlineData("cron", "", "", false)]
    [InlineData("cron one two", "", "", false)]
    [InlineData("cron RodRT", "cron", "RodRT", true)]
    [InlineData("  runkey   RodRun  ", "runkey", "RodRun", true)]
    public void TryParseRemoveArgs_Routes_Fields(string input, string mech, string name, bool ok)
    {
        var result = Persist.TryParseRemoveArgs(input, out var m, out var n);
        Assert.Equal(ok, result);
        Assert.Equal(mech, m);
        Assert.Equal(name, n);
    }
}
