using Rod.CoreState.Implants;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of the verb-to-technique mapping data file and its loader
/// (architecture.md Sec 15.3, ADR-007): the embedded <c>attack-coverage.json</c>
/// is first-class data -- the one source the coverage matrix, the Navigator
/// layer export, and the gap queue read -- so its invariants (every id
/// well-formed, every mapping resolvable, every technique named and tacked to
/// tactics) must hold, not be assumed.
/// </summary>
public class AttackTechniqueCatalogTests
{
    [Fact]
    public void Verbs_EveryFrameworkCategory_IsMappedOrDeliberatelyAbsent()
    {
        // The mapped set is the framework verbs with an honest technique. The
        // deliberate absences are as much a decision as the entries: process
        // termination and cadence retuning have no ATT&CK technique, and the
        // plugin seam's own family plus persistence bookkeeping and exploit
        // module registration are plumbing, not tradecraft.
        Assert.Equal(
            new[]
            {
                "shell.exec", "shell.interact", "file.push", "file.pull", "fs.list",
                "tunnel.forward", "tunnel.socks",
                "recon.portscan", "recon.hostenum", "recon.service", "recon.ps",
                "lateral.move", "lateral.token", "lateral.exec_remote",
                "persist.install",
                "collect.cred", "collect.keylog", "collect.screenshot", "collect.minidump",
                "inject.shellcode",
                "exfil.push", "exfil.stage",
                "browser.fingerprint", "browser.cookies", "browser.dom",
                "browser.screenshot", "browser.prompt",
                "evasion.avoid", "evasion.unload",
                "exploit.invoke",
            }.OrderBy(v => v, StringComparer.Ordinal),
            AttackTechniqueCatalog.Verbs.Keys.OrderBy(v => v, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("shell.exec", new[] { "T1059.003", "T1059.004" })]
    [InlineData("recon.hostenum", new[] { "T1082" })]
    [InlineData("persist.install", new[] { "T1053", "T1543", "T1547" })]
    [InlineData("browser.cookies", new[] { "T1539", "T1555.003" })]
    [InlineData("tunnel.socks", new[] { "T1090" })]
    public void Verbs_TheMapping_HoldsTheDeclaredTechniques(string verb, string[] expected)
        => Assert.Equal(expected, AttackTechniqueCatalog.Verbs[verb]);

    [Fact]
    public void Verbs_MatchesCaseInsensitively()
        => Assert.NotNull(AttackTechniqueCatalog.Verbs["SHELL.EXEC"]);

    [Fact]
    public void Verbs_EveryMappedId_ResolvesInTheTechniquesHalf()
    {
        // The two halves are one review unit: a mapping to an undeclared
        // technique is a load failure, so this holds by construction -- the
        // test pins the invariant against regression in the loader.
        foreach (var (_, ids) in AttackTechniqueCatalog.Verbs)
        {
            foreach (var id in ids)
                Assert.NotNull(AttackTechniqueCatalog.Find(id));
        }
    }

    [Fact]
    public void Techniques_EveryEntry_CarriesANameAndAtLeastOneTactic()
    {
        Assert.NotEmpty(AttackTechniqueCatalog.Techniques);
        foreach (var technique in AttackTechniqueCatalog.Techniques)
        {
            Assert.False(string.IsNullOrWhiteSpace(technique.Name));
            Assert.NotEmpty(technique.Tactics);
        }
    }

    [Fact]
    public void Find_MatchesCaseInsensitively_AndUnknownIdsReturnNull()
    {
        Assert.NotNull(AttackTechniqueCatalog.Find("t1082"));
        Assert.Null(AttackTechniqueCatalog.Find("T9999"));
    }

    [Fact]
    public void AttackVersion_IsPinned()
        => Assert.False(string.IsNullOrWhiteSpace(AttackTechniqueCatalog.AttackVersion));

    [Fact]
    public void DefaultResolver_ReturnsTheFileMapping_AndNullForUnmappedVerbs()
    {
        IVerbTechniqueResolver resolver = new DefaultVerbTechniqueResolver();
        Assert.Equal(new[] { "T1082" }, resolver.Techniques("recon.hostenum"));
        Assert.Null(resolver.Techniques("proc.kill"));
        Assert.Null(resolver.Techniques("no.such.verb"));
    }
}
