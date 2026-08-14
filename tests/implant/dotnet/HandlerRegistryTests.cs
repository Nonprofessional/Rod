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
        "recon.portscan",
        "recon.hostenum",
        "recon.service",
        "lateral.move",
        "lateral.token",
        "lateral.exec_remote",
        "persist.install",
        "persist.remove",
        "persist.list",
        "collect.file",
        "collect.cred",
        "exfil.push",
        "exfil.stage",
    };

    // The full stage-2 reduced verb set (architecture.md Sec 5.2). The verbs
    // with no compiled handler in the reference implant (file.push, file.pull,
    // tunnel.open, probe.read, collect.keylog) must drop out of the advertised
    // set -- that is the whole point of the intersection.
    private static readonly string[] Stage2ClassVerbs =
    {
        "shell.exec", "file.push", "file.pull", "tunnel.open", "probe.read",
        "recon.portscan", "recon.hostenum", "recon.service",
        "lateral.move", "lateral.token", "lateral.exec_remote",
        "persist.install", "persist.remove", "persist.list",
        "collect.file", "collect.cred", "collect.keylog",
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
        Assert.DoesNotContain("file.push", advertised);
        Assert.DoesNotContain("probe.read", advertised);
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
    public void AdvertisedVerbs_WebShellBake_DropsUnimplementedVerbs()
    {
        // A web-shell class permits shell.exec and probe.read; the reference
        // implant implements only shell.exec, so probe.read must not be
        // advertised.
        var registry = HandlerRegistry.Default(enroll: null);
        var advertised = registry.AdvertisedVerbs(new[] { "shell.exec", "probe.read" });
        Assert.Equal(new[] { "shell.exec" }, advertised);
    }

    [Fact]
    public void AdvertisedVerbs_StagerBake_IsEmptyWhenNothingIsImplemented()
    {
        // A stager class permits only file.pull, which the reference implant
        // does not implement: the advertised set is empty, never a lie.
        var registry = HandlerRegistry.Default(enroll: null);
        Assert.Empty(registry.AdvertisedVerbs(new[] { "file.pull" }));
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
                new CapabilityHandler("probe.read",
                    _ => new HandlerResult(TaskOutcome.Succeeded, "host facts", Array.Empty<ExfilChunk>())),
            });
        var advertised = registry.AdvertisedVerbs(new[] { "shell.exec", "probe.read" });
        Assert.Equal(new[] { "shell.exec", "probe.read" }, advertised);
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
