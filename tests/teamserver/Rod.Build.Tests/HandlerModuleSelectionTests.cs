using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState.Implants;

namespace Rod.Build.Tests;

/// <summary>
/// Unit tests for the bake-time handler trim (architecture.md Sec 5.2/5.3):
/// the build class's verb set decides which handler sources compile, so a
/// reduced class is a genuinely reduced binary. The selection is pinned
/// against every class shape (the full stage-2 set, the shell-only classes,
/// the tunnel-only pivot), the staging rewrite is pinned file by file, and
/// the verb rule the extension overlay rides -- a gated verb the class
/// withholds stays out; ungated and unknown verbs ride every build -- is
/// pinned against the keylog verb the acceptance names.
/// </summary>
public class HandlerModuleSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rod-handler-trim-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Select_AFullClass_DropsNothingAndRegistersEverything()
    {
        // Stage-2 carries every reference verb, so its build compiles the
        // whole handler set and registers it in the stub's order -- the full
        // class is the unchanged shape the trim must preserve.
        var selection = HandlerModuleSelection.Select(ImplantClass.Stage2);

        Assert.Empty(selection.DroppedSourceFiles);
        Assert.Equal(
            new[]
            {
                "shell.exec", "shell.interact", "beacon.sleep", "tunnel.forward", "tunnel.socks",
                "file.push", "file.pull", "fs.list", "proc.kill",
                "recon.portscan", "recon.hostenum", "recon.service", "recon.ps",
                "lateral.move", "lateral.token", "lateral.exec_remote",
                "persist.install", "persist.remove", "persist.list",
                "collect.cred", "collect.screenshot",
                "exfil.push", "exfil.stage",
            },
            selection.RegisteredVerbs);
    }

    [Theory]
    [InlineData(ImplantClass.WebShell)]
    [InlineData(ImplantClass.Ephemeral)]
    public void Select_AShellOnlyClass_KeepsTheShellSourceAlone(ImplantClass @class)
    {
        // The one-verb classes keep the source serving shell.exec (whole, with
        // the recon verbs' code -- the trim is per file) and drop every other
        // handler source; only shell.exec registers.
        var selection = HandlerModuleSelection.Select(@class);

        Assert.Equal(new[] { "shell.exec" }, selection.RegisteredVerbs);
        Assert.Equal(
            new[]
            {
                "Internal/BeaconSleep.cs",
                "Internal/Collect.cs",
                "Internal/Exfil.cs",
                "Internal/Files.cs",
                "Internal/InteractiveShell.cs",
                "Internal/Lateral.cs",
                "Internal/Persist.cs",
                "Internal/Png.cs",
                "Internal/Proc.cs",
                "Internal/ScreenCapture.cs",
                "Internal/TunnelForward.cs",
                "Internal/TunnelSocks.cs",
            },
            selection.DroppedSourceFiles);
    }

    [Fact]
    public void Select_ATunnelOnlyClass_KeepsTheTunnelSourcesAlone()
    {
        // The pivot forwards, it does not shell: exactly the tunnel sources
        // stay, and only the tunnel verbs (one-shot fallbacks and channels)
        // register.
        var selection = HandlerModuleSelection.Select(ImplantClass.Pivot);

        Assert.Equal(new[] { "tunnel.forward", "tunnel.socks" }, selection.RegisteredVerbs);
        Assert.Equal(
            new[]
            {
                "Internal/BeaconSleep.cs",
                "Internal/Collect.cs",
                "Internal/Exec.cs",
                "Internal/Exfil.cs",
                "Internal/Files.cs",
                "Internal/InteractiveShell.cs",
                "Internal/Lateral.cs",
                "Internal/Persist.cs",
                "Internal/Png.cs",
                "Internal/Proc.cs",
                "Internal/ScreenCapture.cs",
            },
            selection.DroppedSourceFiles);
    }

    [Theory]
    [InlineData(ImplantClass.Stage2, "collect.keylog", true)]
    [InlineData(ImplantClass.WebShell, "collect.keylog", false)]
    [InlineData(ImplantClass.Ephemeral, "collect.keylog", false)]
    [InlineData(ImplantClass.Pivot, "collect.keylog", false)]
    [InlineData(ImplantClass.Stage2, "shell.exec", true)]
    [InlineData(ImplantClass.Pivot, "shell.exec", false)]
    [InlineData(ImplantClass.Pivot, "tunnel.socks", true)]
    [InlineData(ImplantClass.WebShell, "recon.portscan", false)]
    [InlineData(ImplantClass.Pivot, "evasion.avoid", true)]
    [InlineData(ImplantClass.WebShell, "exploit.invoke", true)]
    [InlineData(ImplantClass.Pivot, "demo.ping", true)]
    [InlineData(ImplantClass.Stage2, null, true)]
    public void CompilesVerb_FollowsTheClassTable(ImplantClass @class, string? verb, bool expected)
    {
        // The extension overlay's rule: a verb the class table gates compiles
        // only into builds whose class carries it -- keylogging, gated to
        // stage-2, stays out of every reduced class -- while the ungated
        // contract verbs (evasion, exploit) and any verb no class lists (an
        // operator's own) ride every build, keeping the kit's drop-in promise.
        // A verb the scan cannot read (null) never drops.
        Assert.Equal(expected, HandlerModuleSelection.CompilesVerb(@class, verb));
    }

    [Fact]
    public void Apply_AReducedClass_RemovesTheHandlerSources_AndWritesTheSelection()
    {
        var staging = StageHandlerFiles();

        HandlerModuleSelection.Apply(staging, HandlerModuleSelection.Select(ImplantClass.WebShell));

        // Whole source files out: every handler file the shell-only class
        // does not need leaves the compilation; the shell source stays.
        Assert.True(File.Exists(Path.Combine(staging, "Internal", "Exec.cs")));
        foreach (var dropped in new[]
                 {
                     "BeaconSleep.cs", "Files.cs", "Proc.cs", "Lateral.cs", "Persist.cs", "Collect.cs", "Exfil.cs",
                     "InteractiveShell.cs", "TunnelForward.cs", "TunnelSocks.cs", "ScreenCapture.cs", "Png.cs",
                 })
            Assert.False(File.Exists(Path.Combine(staging, "Internal", dropped)), dropped + " must leave the compilation");

        // The generated selection registers only the kept verb, in the stub's
        // shape: no file transfer, no staged arm, no channels.
        var selection = File.ReadAllText(Path.Combine(staging, "Internal", "HandlerSelection.cs"));
        Assert.Contains("new CapabilityHandler(\"shell.exec\", args => Core.ShellExec(args)),", selection);
        Assert.DoesNotContain("Files.Push", selection);
        Assert.DoesNotContain("Lateral.Move", selection);
        Assert.DoesNotContain("InteractiveShell.RunAsync", selection);
        Assert.DoesNotContain("TunnelForward.RunAsync", selection);
    }

    [Fact]
    public void Apply_ATunnelOnlyClass_WritesTheTunnelRegistrations()
    {
        var staging = StageHandlerFiles();

        HandlerModuleSelection.Apply(staging, HandlerModuleSelection.Select(ImplantClass.Pivot));

        // The pivot's channels and one-shot fallbacks name only the tunnel
        // verbs; the interactive shell's registration is absent with its
        // source.
        var selection = File.ReadAllText(Path.Combine(staging, "Internal", "HandlerSelection.cs"));
        Assert.Contains("new(\"tunnel.forward\", (args, stream, ct) => TunnelForward.RunAsync(args, stream, ct)),", selection);
        Assert.Contains("new(\"tunnel.socks\", (args, stream, ct) => TunnelSocks.RunAsync(args, stream, ct)),", selection);
        Assert.Contains("\"tunnel.forward runs as a live channel", selection);
        Assert.DoesNotContain("InteractiveShell", selection);
        Assert.DoesNotContain("Core.ShellExec", selection);
        Assert.DoesNotContain("Files.PushStaged", selection);
    }

    [Fact]
    public void Apply_AFullClass_KeepsEveryFile_AndRegistersTheWholeSet()
    {
        var staging = StageHandlerFiles();

        HandlerModuleSelection.Apply(staging, HandlerModuleSelection.Select(ImplantClass.Stage2));

        foreach (var file in new[]
                 {
                     "Exec.cs", "BeaconSleep.cs", "Files.cs", "Proc.cs", "Lateral.cs", "Persist.cs", "Collect.cs", "Exfil.cs",
                     "InteractiveShell.cs", "TunnelForward.cs", "TunnelSocks.cs", "ScreenCapture.cs", "Png.cs",
                 })
            Assert.True(File.Exists(Path.Combine(staging, "Internal", file)), file + " must survive a full-class bake");

        // The full-class generation carries the stub's registrations,
        // including the enroll-capturing lateral.move, the staged arm, the
        // file browser's listing verb, the cadence-capturing beacon.sleep,
        // and every channel.
        var selection = File.ReadAllText(Path.Combine(staging, "Internal", "HandlerSelection.cs"));
        Assert.Contains("new CapabilityHandler(\"lateral.move\", args => Lateral.Move(args, enroll)),", selection);
        Assert.Contains("new CapabilityHandler(\"fs.list\", args => Files.List(args)),", selection);
        Assert.Contains("new CapabilityHandler(\"beacon.sleep\", args => BeaconSleep.Set(args, cadence)),", selection);
        Assert.Contains("(\"file.push\", (args, data) => Files.PushStaged(args, data)),", selection);
        Assert.Contains("new(\"shell.interact\", (args, stream, ct) => InteractiveShell.RunAsync(args, stream, ct)),", selection);
    }

    // Stages a minimal implant tree: every handler source the trim knows plus
    // the HandlerSelection stub, the files Apply is contractually allowed to
    // touch.
    private string StageHandlerFiles()
    {
        var staging = Path.Combine(_root, "staging", "dotnet");
        Directory.CreateDirectory(Path.Combine(staging, "Internal"));
        foreach (var file in new[]
                 {
                     "Exec.cs", "BeaconSleep.cs", "Files.cs", "Proc.cs", "Lateral.cs", "Persist.cs", "Collect.cs", "Exfil.cs",
                     "InteractiveShell.cs", "TunnelForward.cs", "TunnelSocks.cs", "ScreenCapture.cs", "Png.cs",
                     "HandlerSelection.cs",
                 })
            File.WriteAllText(Path.Combine(staging, "Internal", file), "// fixture");
        return staging;
    }
}
