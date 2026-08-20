using Rod.CoreState.Implants;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of <see cref="ImplantClassCapabilities"/> -- the per-class reduced
/// verb set the teamserver gates tasking on (architecture.md Sec 5.2). Each
/// class advertises the verbs its operational purpose justifies; a stage-2
/// implant carries the full core set plus the tunnel set, the recon set, the
/// lateral set, the persist set, the collect set, and the exfil set, every
/// other class a subset (and no recon, lateral, persist, collect, or exfil
/// verbs) -- the pivot class carries exactly the tunnel set.
/// </summary>
public class ImplantClassCapabilitiesTests
{
    [Theory]
    [InlineData(ImplantClass.Stage2, "shell.exec")]
    [InlineData(ImplantClass.Stage2, "shell.interact")]
    [InlineData(ImplantClass.Stage2, "file.push")]
    [InlineData(ImplantClass.Stage2, "file.pull")]
    [InlineData(ImplantClass.Stage2, "proc.kill")]
    [InlineData(ImplantClass.Stage2, "tunnel.forward")]
    [InlineData(ImplantClass.Stage2, "tunnel.socks")]
    [InlineData(ImplantClass.Stage2, "recon.portscan")]
    [InlineData(ImplantClass.Stage2, "recon.hostenum")]
    [InlineData(ImplantClass.Stage2, "recon.service")]
    [InlineData(ImplantClass.Stage2, "recon.ps")]
    [InlineData(ImplantClass.Stage2, "lateral.move")]
    [InlineData(ImplantClass.Stage2, "lateral.token")]
    [InlineData(ImplantClass.Stage2, "lateral.exec_remote")]
    [InlineData(ImplantClass.Stage2, "persist.install")]
    [InlineData(ImplantClass.Stage2, "persist.remove")]
    [InlineData(ImplantClass.Stage2, "persist.list")]
    [InlineData(ImplantClass.Stage2, "collect.cred")]
    [InlineData(ImplantClass.Stage2, "collect.keylog")]
    [InlineData(ImplantClass.Stage2, "collect.screenshot")]
    [InlineData(ImplantClass.Stage2, "exfil.push")]
    [InlineData(ImplantClass.Stage2, "exfil.stage")]
    [InlineData(ImplantClass.Stager, "file.pull")]
    [InlineData(ImplantClass.WebShell, "shell.exec")]
    [InlineData(ImplantClass.Ephemeral, "shell.exec")]
    [InlineData(ImplantClass.Pivot, "tunnel.forward")]
    [InlineData(ImplantClass.Pivot, "tunnel.socks")]
    public void Allows_AdmitsTheReducedVerbSetForTheClass(ImplantClass @class, string verb)
        => Assert.True(ImplantClassCapabilities.Allows(@class, verb));

    [Theory]
    [InlineData(ImplantClass.Stager, "shell.exec", "a stager only pulls")]
    [InlineData(ImplantClass.Stager, "recon.portscan", "recon is a stage-2 long-haul activity")]
    [InlineData(ImplantClass.Stager, "lateral.move", "lateral movement is a stage-2 activity")]
    [InlineData(ImplantClass.Stager, "persist.install", "persistence is a stage-2 activity")]
    [InlineData(ImplantClass.Stager, "collect.cred", "collection and exfiltration are stage-2 long-haul activities")]
    [InlineData(ImplantClass.Stager, "proc.kill", "process termination joins stage-2's core operations")]
    [InlineData(ImplantClass.Stager, "tunnel.forward", "tunneling joins stage-2's core operations and the pivot set")]
    [InlineData(ImplantClass.WebShell, "file.push", "a web-shell does not push")]
    [InlineData(ImplantClass.WebShell, "recon.hostenum", "recon is a stage-2 long-haul activity")]
    [InlineData(ImplantClass.WebShell, "lateral.token", "lateral movement is a stage-2 activity")]
    [InlineData(ImplantClass.WebShell, "persist.list", "persistence is a stage-2 activity")]
    [InlineData(ImplantClass.WebShell, "exfil.push", "collection and exfiltration are stage-2 long-haul activities")]
    [InlineData(ImplantClass.WebShell, "recon.ps", "process listing is a stage-2 long-haul activity")]
    [InlineData(ImplantClass.WebShell, "tunnel.forward", "tunneling joins stage-2's core operations and the pivot set")]
    [InlineData(ImplantClass.Ephemeral, "file.push", "an ephemeral does not push")]
    [InlineData(ImplantClass.Ephemeral, "recon.service", "recon is a stage-2 long-haul activity")]
    [InlineData(ImplantClass.Ephemeral, "lateral.exec_remote", "lateral movement is a stage-2 activity")]
    [InlineData(ImplantClass.Ephemeral, "persist.remove", "persistence is a stage-2 activity")]
    [InlineData(ImplantClass.Ephemeral, "collect.cred", "collection and exfiltration are stage-2 long-haul activities")]
    [InlineData(ImplantClass.Ephemeral, "collect.screenshot", "collection and exfiltration are stage-2 long-haul activities")]
    [InlineData(ImplantClass.Ephemeral, "tunnel.forward", "tunneling joins stage-2's core operations and the pivot set")]
    [InlineData(ImplantClass.Pivot, "shell.exec", "a pivot forwards, it does not shell")]
    [InlineData(ImplantClass.Pivot, "recon.portscan", "recon is a stage-2 long-haul activity")]
    [InlineData(ImplantClass.Pivot, "lateral.move", "lateral movement is a stage-2 activity")]
    [InlineData(ImplantClass.Pivot, "persist.install", "persistence is a stage-2 activity")]
    [InlineData(ImplantClass.Pivot, "exfil.stage", "collection and exfiltration are stage-2 long-haul activities")]
    public void Allows_DeniesAVerbOutsideTheClassSet(ImplantClass @class, string verb, string rationale)
    {
        _ = rationale; // documents the case; not asserted.
        Assert.False(ImplantClassCapabilities.Allows(@class, verb));
    }

    [Fact]
    public void Allows_MatchesCaseInsensitively()
        => Assert.True(ImplantClassCapabilities.Allows(ImplantClass.Stage2, "SHELL.EXEC"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Allows_RejectsABlankVerb(string? verb)
        => Assert.False(ImplantClassCapabilities.Allows(ImplantClass.Stage2, verb));

    [Fact]
    public void For_Stage2_ReturnsTheFullCoreTunnelReconLateralPersistCollectAndExfilSet()
    {
        // Stage-2 is the primary long-haul implant: it carries the full core set
        // plus the tunnel set, the recon set, the lateral set, the persist set,
        // the collect set, and the exfil set, since tunneling is a core
        // operation (architecture.md Sec 14) and recon, lateral movement,
        // persistence, collection, and exfiltration are long-haul activities
        // (architecture.md Sec 5.2, Sec 10.1). Every other class carries a subset
        // for its purpose.
        var verbs = ImplantClassCapabilities.For(ImplantClass.Stage2);
        Assert.Equal(
            new[]
            {
                "shell.exec", "shell.interact", "file.push", "file.pull", "proc.kill",
                "tunnel.forward", "tunnel.socks",
                "recon.portscan", "recon.hostenum", "recon.service", "recon.ps",
                "lateral.move", "lateral.token", "lateral.exec_remote",
                "persist.install", "persist.remove", "persist.list",
                "collect.cred", "collect.keylog", "collect.screenshot",
                "exfil.push", "exfil.stage",
            },
            verbs);
    }

    [Fact]
    public void For_ReturnsTheSharedSet_NotACopyPerCall()
    {
        // The same read-only reference is returned for a class, so callers
        // cannot mutate it and there is no per-call allocation.
        Assert.Same(
            ImplantClassCapabilities.For(ImplantClass.Stage2),
            ImplantClassCapabilities.For(ImplantClass.Stage2));
    }

    [Fact]
    public void Ungated_IsExactlyTheEvasionAndExploitContractVerbs()
    {
        // The contract-only verbs no class gates (architecture.md Sec 5.2,
        // Sec 10.2): the evasion and exploit categories in their entirety,
        // decided per deployment rather than per class.
        Assert.Equal(
            new[] { "evasion.avoid", "evasion.unload", "exploit.invoke", "exploit.module" },
            ImplantClassCapabilities.Ungated);
    }

    [Fact]
    public void Ungated_VerbsAppearInNoClassSet()
    {
        // The whole point of the ungated list: no class table entry carries any
        // of these verbs, so the only way a baked artifact may run one is the
        // ungated contract list riding along in the bake (the task gate admits
        // them through the registry-backed resolver instead, Sec 10.3).
        foreach (ImplantClass @class in Enum.GetValues(typeof(ImplantClass)))
        {
            foreach (var verb in ImplantClassCapabilities.Ungated)
                Assert.False(ImplantClassCapabilities.Allows(@class, verb));
        }
    }

    [Fact]
    public void For_EveryClassReturnsVerbs_PivotCarriesExactlyTheTunnelSet()
    {
        // Every class carries at least one verb. Pivot is the tunneling class
        // (architecture.md Sec 5.2): exactly the tunnel set -- enough to forward
        // traffic for hosts that cannot run their own implant, and nothing a
        // long-haul stage-2 footprint justifies.
        foreach (ImplantClass @class in Enum.GetValues(typeof(ImplantClass)))
            Assert.NotEmpty(ImplantClassCapabilities.For(@class));
        Assert.Equal(
            new[] { "tunnel.forward", "tunnel.socks" },
            ImplantClassCapabilities.For(ImplantClass.Pivot));
    }
}
