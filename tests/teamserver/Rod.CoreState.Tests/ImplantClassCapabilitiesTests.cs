using Rod.CoreState.Implants;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of <see cref="ImplantClassCapabilities"/> -- the per-class reduced
/// verb set the teamserver gates tasking on (architecture.md Sec 5.2). Each
/// class advertises the verbs its operational purpose justifies; a stage-2
/// implant carries the full core set plus the recon set, the lateral set, the
/// persist set, the collect set, and the exfil set, every other class a subset
/// (and no recon, lateral, persist, collect, or exfil verbs).
/// </summary>
public class ImplantClassCapabilitiesTests
{
    [Theory]
    [InlineData(ImplantClass.Stage2, "shell.exec")]
    [InlineData(ImplantClass.Stage2, "shell.interact")]
    [InlineData(ImplantClass.Stage2, "file.push")]
    [InlineData(ImplantClass.Stage2, "file.pull")]
    [InlineData(ImplantClass.Stage2, "recon.portscan")]
    [InlineData(ImplantClass.Stage2, "recon.hostenum")]
    [InlineData(ImplantClass.Stage2, "recon.service")]
    [InlineData(ImplantClass.Stage2, "lateral.move")]
    [InlineData(ImplantClass.Stage2, "lateral.token")]
    [InlineData(ImplantClass.Stage2, "lateral.exec_remote")]
    [InlineData(ImplantClass.Stage2, "persist.install")]
    [InlineData(ImplantClass.Stage2, "persist.remove")]
    [InlineData(ImplantClass.Stage2, "persist.list")]
    [InlineData(ImplantClass.Stage2, "collect.cred")]
    [InlineData(ImplantClass.Stage2, "collect.keylog")]
    [InlineData(ImplantClass.Stage2, "exfil.push")]
    [InlineData(ImplantClass.Stage2, "exfil.stage")]
    [InlineData(ImplantClass.Stager, "file.pull")]
    [InlineData(ImplantClass.WebShell, "shell.exec")]
    [InlineData(ImplantClass.Ephemeral, "shell.exec")]
    public void Allows_AdmitsTheReducedVerbSetForTheClass(ImplantClass @class, string verb)
        => Assert.True(ImplantClassCapabilities.Allows(@class, verb));

    [Theory]
    [InlineData(ImplantClass.Stager, "shell.exec", "a stager only pulls")]
    [InlineData(ImplantClass.Stager, "recon.portscan", "recon is a stage-2 long-haul activity")]
    [InlineData(ImplantClass.Stager, "lateral.move", "lateral movement is a stage-2 activity")]
    [InlineData(ImplantClass.Stager, "persist.install", "persistence is a stage-2 activity")]
    [InlineData(ImplantClass.Stager, "collect.cred", "collection and exfiltration are stage-2 long-haul activities")]
    [InlineData(ImplantClass.WebShell, "file.push", "a web-shell does not push")]
    [InlineData(ImplantClass.WebShell, "recon.hostenum", "recon is a stage-2 long-haul activity")]
    [InlineData(ImplantClass.WebShell, "lateral.token", "lateral movement is a stage-2 activity")]
    [InlineData(ImplantClass.WebShell, "persist.list", "persistence is a stage-2 activity")]
    [InlineData(ImplantClass.WebShell, "exfil.push", "collection and exfiltration are stage-2 long-haul activities")]
    [InlineData(ImplantClass.Ephemeral, "file.push", "an ephemeral does not push")]
    [InlineData(ImplantClass.Ephemeral, "recon.service", "recon is a stage-2 long-haul activity")]
    [InlineData(ImplantClass.Ephemeral, "lateral.exec_remote", "lateral movement is a stage-2 activity")]
    [InlineData(ImplantClass.Ephemeral, "persist.remove", "persistence is a stage-2 activity")]
    [InlineData(ImplantClass.Ephemeral, "collect.cred", "collection and exfiltration are stage-2 long-haul activities")]
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
    public void For_Stage2_ReturnsTheFullCoreReconLateralPersistCollectAndExfilSet()
    {
        // Stage-2 is the primary long-haul implant: it carries the full core set
        // plus the recon set, the lateral set, the persist set, the collect set,
        // and the exfil set, since recon, lateral movement, persistence,
        // collection, and exfiltration are long-haul activities
        // (architecture.md Sec 5.2, Sec 10.1). Every other class carries a subset
        // for its purpose and no recon, lateral, persist, collect, or exfil verbs.
        var verbs = ImplantClassCapabilities.For(ImplantClass.Stage2);
        Assert.Equal(
            new[]
            {
                "shell.exec", "shell.interact", "file.push", "file.pull",
                "recon.portscan", "recon.hostenum", "recon.service",
                "lateral.move", "lateral.token", "lateral.exec_remote",
                "persist.install", "persist.remove", "persist.list",
                "collect.cred", "collect.keylog",
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
    public void For_EveryImplementedClassReturnsVerbs_PivotIsReservedEmpty()
    {
        // Every class with a shipped artifact carries at least one verb. Pivot
        // is reserved for tunneling artifacts: nothing ships for it yet, so its
        // set is empty and it admits nothing (architecture.md Sec 5.2).
        foreach (ImplantClass @class in Enum.GetValues(typeof(ImplantClass)))
        {
            if (@class == ImplantClass.Pivot)
                Assert.Empty(ImplantClassCapabilities.For(@class));
            else
                Assert.NotEmpty(ImplantClassCapabilities.For(@class));
        }
    }
}
