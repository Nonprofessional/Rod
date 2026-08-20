using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// Pins the implant-side capability pluggability contract (architecture.md
// Sec 5.3): the reference registry holds exactly the compiled handlers, the
// advertised set is the baked class verbs intersected with those handlers, and
// the reference set contains no Sec 13 boundary verb. The stage-2 class list is
// quoted from the server's authority (Rod.CoreState.ImplantClassCapabilities)
// so a drift between the two halves fails here.
public class HandlerRegistryTests
{
    // The verbs the reference implant compiles handlers for -- the exact set a
    // stage-2 bake intersects down to, and the set a dev (un-baked) binary
    // advertises wholesale.
    private static readonly string[] ReferenceVerbs =
    {
        "shell.exec",
        "shell.interact",
        "file.push",
        "file.pull",
        "proc.kill",
        "tunnel.forward",
        "tunnel.socks",
        "recon.portscan",
        "recon.hostenum",
        "recon.service",
        "recon.ps",
        "lateral.move",
        "lateral.token",
        "lateral.exec_remote",
        "persist.install",
        "persist.remove",
        "persist.list",
        "collect.cred",
        "collect.screenshot",
        "exfil.push",
        "exfil.stage",
    };

    // The full stage-2 reduced verb set (architecture.md Sec 5.2). A verb in
    // it with no compiled handler in the reference implant (collect.keylog is
    // the one today) must drop out of the advertised set -- that is the whole
    // point of the intersection.
    private static readonly string[] Stage2ClassVerbs =
    {
        "shell.exec", "shell.interact", "file.push", "file.pull", "proc.kill", "tunnel.forward", "tunnel.socks",
        "recon.portscan", "recon.hostenum", "recon.service", "recon.ps",
        "lateral.move", "lateral.token", "lateral.exec_remote",
        "persist.install", "persist.remove", "persist.list",
        "collect.cred", "collect.keylog", "collect.screenshot",
        "exfil.push", "exfil.stage",
    };

    [Fact]
    public void Default_RegistersExactlyTheReferenceVerbSet()
    {
        var registry = HandlerRegistry.Default(enroll: null);
        Assert.Equal(ReferenceVerbs.OrderBy(v => v, StringComparer.Ordinal), registry.Verbs.OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void Default_ContainsNoSec13BoundaryVerb()
    {
        // Contract-only verbs (architecture.md Sec 10.2, Sec 13): collect.keylog
        // (input capture) and the evasion/exploit categories. The reference
        // registry ships no handler for any of them.
        var registry = HandlerRegistry.Default(enroll: null);
        var verbs = registry.Verbs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("collect.keylog", verbs);
        Assert.DoesNotContain("evasion.avoid", verbs);
        Assert.DoesNotContain("evasion.unload", verbs);
        Assert.DoesNotContain("exploit.invoke", verbs);
        Assert.DoesNotContain("exploit.module", verbs);
    }

    [Fact]
    public void AdvertisedVerbs_Stage2Bake_IntersectsClassVerbsWithHandlers()
    {
        var registry = HandlerRegistry.Default(enroll: null);
        var advertised = registry.AdvertisedVerbs(Stage2ClassVerbs);
        // Exactly the compiled stage-2 subset, never a verb without a handler.
        Assert.Equal(ReferenceVerbs.OrderBy(v => v, StringComparer.Ordinal), advertised.OrderBy(v => v, StringComparer.Ordinal));
        Assert.DoesNotContain("collect.keylog", advertised);
    }

    [Fact]
    public void AdvertisedVerbs_EmptyBake_IsTheFullCompiledSet()
    {
        // The checked-in dev stub bakes no class verbs; an un-baked binary
        // advertises everything it can run and nothing else.
        var registry = HandlerRegistry.Default(enroll: null);
        Assert.Equal(ReferenceVerbs.OrderBy(v => v, StringComparer.Ordinal), registry.AdvertisedVerbs(Array.Empty<string>()).OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void AdvertisedVerbs_NarrowBake_DropsVerbsOutsideIt()
    {
        // A web-shell class permits only shell.exec; verbs it does not permit
        // (recon.hostenum, compiled and all) must not be advertised.
        var registry = HandlerRegistry.Default(enroll: null);
        Assert.Equal(new[] { "shell.exec" }, registry.AdvertisedVerbs(new[] { "shell.exec" }));
    }

    [Fact]
    public void AdvertisedVerbs_StagerBake_IsExactlyTheFetchVerb()
    {
        // A stager class permits only file.pull -- the fetch a stage-1 loader
        // needs -- and the reference implant implements it, so that is exactly
        // what a stager bake advertises.
        var registry = HandlerRegistry.Default(enroll: null);
        Assert.Equal(new[] { "file.pull" }, registry.AdvertisedVerbs(new[] { "file.pull" }));
    }

    [Fact]
    public void ChannelFor_ResolvesTheChannelArm_AndNullsEverythingElse()
    {
        // The streaming task shape's registrations (architecture.md Sec 10.3):
        // shell.interact and tunnel.forward are channel verbs, every one-shot
        // verb is not.
        var registry = HandlerRegistry.Default(enroll: null);
        Assert.NotNull(registry.ChannelFor("shell.interact"));
        Assert.NotNull(registry.ChannelFor("tunnel.forward"));
        Assert.Null(registry.ChannelFor("shell.exec"));
        Assert.Null(registry.ChannelFor("file.push"));
    }

    [Fact]
    public void Dispatch_ChannelVerbOnTheOneShotPath_FailsCleanly()
    {
        // A dispatch path with no channel to carry it (a poll cycle, a future
        // stream-less transport) must refuse the verb at the registry rather
        // than losing it -- the fallback registration keeps the verb
        // dispatchable everywhere the registry is used.
        var registry = HandlerRegistry.Default(enroll: null);
        var (shellOutcome, shellOutput, _) = registry.Dispatch("shell.interact", "");
        Assert.Equal(TaskOutcome.Failed, shellOutcome);
        Assert.Contains("live channel", shellOutput);
        var (tunnelOutcome, tunnelOutput, _) = registry.Dispatch("tunnel.forward", "");
        Assert.Equal(TaskOutcome.Failed, tunnelOutcome);
        Assert.Contains("live channel", tunnelOutput);
    }

    [Fact]
    public void AdvertisedVerbs_PivotBake_IsExactlyTheTunnelSet()
    {
        // The pivot class carries exactly the tunnel set (architecture.md Sec
        // 5.2), and the reference implant compiles the handler for it, so a
        // Pivot-class build advertises tunnel.forward and nothing else -- the
        // minimal tunneling artifact.
        var registry = HandlerRegistry.Default(enroll: null);
        Assert.Equal(new[] { "tunnel.forward" }, registry.AdvertisedVerbs(new[] { "tunnel.forward" }));
    }

    [Fact]
    public void AdvertisedVerbs_AdditionalHandler_WidensTheSet()
    {
        // The growth seam: one extra registration (an out-of-tree handler
        // compiled into a per-engagement artifact) widens the advertised set
        // without touching the beacon loop.
        var registry = HandlerRegistry.Default(
            enroll: null,
            additional: new[]
            {
                new CapabilityHandler("tunnel.open",
                    _ => new HandlerResult(TaskOutcome.Succeeded, "tunnel up", Array.Empty<ExfilChunk>())),
            });
        var advertised = registry.AdvertisedVerbs(new[] { "shell.exec", "tunnel.open" });
        Assert.Equal(new[] { "shell.exec", "tunnel.open" }, advertised);
    }

    [Fact]
    public void AdvertisedVerbs_ContractOnlyHandler_AdvertisesUnderTheBakedContractVerbs()
    {
        // The bake carries the class set plus the ungated contract-only verbs
        // (architecture.md Sec 5.2/10.2), so an out-of-tree evasion handler
        // compiled into the artifact advertises its verb at handshake. The
        // intersection stays the authority: a contract verb with no compiled
        // handler (evasion.unload here) still never appears.
        var registry = HandlerRegistry.Default(
            enroll: null,
            additional: new[]
            {
                new CapabilityHandler("evasion.avoid",
                    _ => new HandlerResult(TaskOutcome.Succeeded, "avoided", Array.Empty<ExfilChunk>())),
            });
        var baked = Stage2ClassVerbs.Concat(new[]
        {
            "evasion.avoid", "evasion.unload", "exploit.invoke", "exploit.module",
        }).ToArray();

        var advertised = registry.AdvertisedVerbs(baked);

        Assert.Contains("evasion.avoid", advertised);
        Assert.DoesNotContain("evasion.unload", advertised);
        Assert.DoesNotContain("exploit.invoke", advertised);
        Assert.DoesNotContain("exploit.module", advertised);
    }

    [Fact]
    public void AdvertisedVerbs_MatchingIsCaseInsensitive()
    {
        // The server's class table matches verbs case-insensitively; the
        // intersection must too, and the advertised verb keeps the registry's
        // canonical casing.
        var registry = HandlerRegistry.Default(enroll: null);
        var advertised = registry.AdvertisedVerbs(new[] { "SHELL.EXEC" });
        Assert.Equal(new[] { "shell.exec" }, advertised);
    }
}
