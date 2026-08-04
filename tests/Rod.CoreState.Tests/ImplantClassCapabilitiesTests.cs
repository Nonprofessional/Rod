using Rod.CoreState.Implants;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of <see cref="ImplantClassCapabilities"/> -- the per-class reduced
/// verb set the teamserver gates tasking on (architecture.md Sec 5.2). Each
/// class advertises the verbs its operational purpose justifies; a stage-2
/// implant carries the full core set, every other class a subset.
/// </summary>
public class ImplantClassCapabilitiesTests
{
    [Theory]
    [InlineData(ImplantClass.Stage2, "shell.exec")]
    [InlineData(ImplantClass.Stage2, "file.push")]
    [InlineData(ImplantClass.Stage2, "file.pull")]
    [InlineData(ImplantClass.Stage2, "tunnel.open")]
    [InlineData(ImplantClass.Stage2, "probe.read")]
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
    [InlineData(ImplantClass.WebShell, "tunnel.open", "a web-shell holds no tunnel")]
    [InlineData(ImplantClass.WebShell, "file.push", "a web-shell does not push")]
    [InlineData(ImplantClass.Ephemeral, "file.push", "an ephemeral does not push")]
    [InlineData(ImplantClass.Pivot, "shell.exec", "a pivot forwards, it does not shell")]
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
    public void For_Stage2_ReturnsTheFullCoreSet()
    {
        var verbs = ImplantClassCapabilities.For(ImplantClass.Stage2);
        Assert.Equal(
            new[] { "shell.exec", "file.push", "file.pull", "tunnel.open", "probe.read" },
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
