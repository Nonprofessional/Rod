using Rod.CoreState.Implants;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of <see cref="ImplantClassCapabilities"/> -- the per-class reduced
/// verb set the teamserver gates tasking on (architecture.md Sec 5.2). Each
/// class advertises the verbs its operational purpose justifies; a stage-2
/// implant carries the full core set plus the recon set and the lateral set,
/// every other class a subset (and no recon or lateral verbs).
/// </summary>
public class ImplantClassCapabilitiesTests
{
    [Theory]
    [InlineData(ImplantClass.Stage2, "shell.exec")]
    [InlineData(ImplantClass.Stage2, "file.push")]
    [InlineData(ImplantClass.Stage2, "file.pull")]
    [InlineData(ImplantClass.Stage2, "tunnel.open")]
    [InlineData(ImplantClass.Stage2, "probe.read")]
    [InlineData(ImplantClass.Stage2, "recon.portscan")]
    [InlineData(ImplantClass.Stage2, "recon.hostenum")]
    [InlineData(ImplantClass.Stage2, "recon.service")]
    [InlineData(ImplantClass.Stage2, "lateral.move")]
    [InlineData(ImplantClass.Stage2, "lateral.token")]
    [InlineData(ImplantClass.Stage2, "lateral.exec_remote")]
    [InlineData(ImplantClass.Stager, "file.pull")]
    [InlineData(ImplantClass.WebShell, "shell.exec")]
    [InlineData(ImplantClass.WebShell, "probe.read")]
    [InlineData(ImplantClass.Ephemeral, "shell.exec")]
    [InlineData(ImplantClass.Ephemeral, "probe.read")]
    [InlineData(ImplantClass.Pivot, "tunnel.open")]
    [InlineData(ImplantClass.Pivot, "probe.read")]
    public void Allows_AdmitsTheReducedVerbSetForTheClass(ImplantClass @class, string verb)
        => Assert.True(ImplantClassCapabilities.Allows(@class, verb));

    [Theory]
    [InlineData(ImplantClass.Stager, "shell.exec", "a stager only pulls")]
    [InlineData(ImplantClass.Stager, "tunnel.open", "a stager holds no tunnel")]
    [InlineData(ImplantClass.Stager, "recon.portscan", "recon is a stage-2 long-haul activity")]
    [InlineData(ImplantClass.Stager, "lateral.move", "lateral movement is a stage-2 activity")]
    [InlineData(ImplantClass.WebShell, "tunnel.open", "a web-shell holds no tunnel")]
    [InlineData(ImplantClass.WebShell, "file.push", "a web-shell does not push")]
    [InlineData(ImplantClass.WebShell, "recon.hostenum", "recon is a stage-2 long-haul activity")]
    [InlineData(ImplantClass.WebShell, "lateral.token", "lateral movement is a stage-2 activity")]
    [InlineData(ImplantClass.Ephemeral, "file.push", "an ephemeral does not push")]
    [InlineData(ImplantClass.Ephemeral, "recon.service", "recon is a stage-2 long-haul activity")]
    [InlineData(ImplantClass.Ephemeral, "lateral.exec_remote", "lateral movement is a stage-2 activity")]
    [InlineData(ImplantClass.Pivot, "shell.exec", "a pivot forwards, it does not shell")]
    [InlineData(ImplantClass.Pivot, "recon.portscan", "recon is a stage-2 long-haul activity")]
    [InlineData(ImplantClass.Pivot, "lateral.move", "lateral movement is a stage-2 activity")]
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
    public void For_Stage2_ReturnsTheFullCoreReconAndLateralSet()
    {
        // Stage-2 is the primary long-haul implant: it carries the full core set
        // plus the recon set and the lateral set, since recon and lateral
        // movement are long-haul activities (architecture.md Sec 5.2, Sec 10.1).
        // Every other class carries a subset for its purpose and no recon or
        // lateral verbs.
        var verbs = ImplantClassCapabilities.For(ImplantClass.Stage2);
        Assert.Equal(
            new[]
            {
                "shell.exec", "file.push", "file.pull", "tunnel.open", "probe.read",
                "recon.portscan", "recon.hostenum", "recon.service",
                "lateral.move", "lateral.token", "lateral.exec_remote",
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
    public void For_EveryClassReturnsAtLeastOneVerb()
    {
        foreach (ImplantClass @class in Enum.GetValues(typeof(ImplantClass)))
            Assert.NotEmpty(ImplantClassCapabilities.For(@class));
    }
}
