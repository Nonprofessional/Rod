using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState.Implants;

namespace Rod.Build.Tests;

/// <summary>
/// Unit tests for the extension kit's overlay step (the implant half,
/// extending/tradecraft.md): the source scan discovers every class whose base
/// list names ICapabilityHandler, helper sources are left alone, and the
/// generated registrations replace the checked-in stub so a handler drops in
/// as a source file and the build carries it. The loud-failure rules -- a
/// missing directory, or one that yields no handler -- are pinned here too:
/// an operator who configures the directory must never get an artifact that
/// silently lacks the handlers they believe it carries.
/// </summary>
public class ImplantExtensionOverlayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rod-overlay-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteExtension(string name, params (string RelativePath, string Content)[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return dir;
    }

    [Fact]
    public void DiscoverHandlers_FindsClassesImplementingTheContract_InAnyNamespace()
    {
        // The authoring shape: a top-level concrete class whose base list names
        // ICapabilityHandler. The namespace is whatever the source declares --
        // the generated registrations qualify each name fully.
        var dir = WriteExtension("mixed",
            ("DemoPing.cs", """
                using Rod.V1;

                namespace MyTradecraft.Demo;

                internal sealed class DemoPingHandler : Rod.Implant.Internal.ICapabilityHandler
                {
                    public string Verb => "demo.ping";
                    public HandlerResult Handle(string arguments) => (TaskOutcome.Succeeded, "pong");
                }
                """),
            ("SameNamespace.cs", """
                namespace Rod.Implant.Internal;

                internal sealed class AvoidHandler : ICapabilityHandler
                {
                    public string Verb => "evasion.avoid";
                    public HandlerResult Handle(string arguments) => (TaskOutcome.Succeeded, "avoided");
                }
                """),
            ("Helpers.cs", """
                namespace MyTradecraft.Demo;

                // A helper class the base list does not name the contract on:
                // it compiles in but never registers.
                internal static class PingHelpers
                {
                    public static string Format(string value) => value.ToUpperInvariant();
                }
                """));

        var handlers = ImplantExtensionOverlay.DiscoverHandlers(dir);

        Assert.Equal(
            new[]
            {
                new ExtensionHandlerType("MyTradecraft.Demo", "DemoPingHandler"),
                new ExtensionHandlerType("Rod.Implant.Internal", "AvoidHandler"),
            },
            handlers);
    }

    [Fact]
    public void DiscoverHandlers_IgnoresBuildOutputDirectories()
    {
        // An operator's extension directory may carry bin/obj from building the
        // extension standalone; that output must not compile into the implant a
        // second time.
        var dir = WriteExtension("with-bin",
            ("Handler.cs", """
                namespace Ext;

                class OneHandler : ICapabilityHandler { }
                """),
            ("bin/Debug/Handler.cs", """
                namespace Ext.Bin;

                class BinnedHandler : ICapabilityHandler { }
                """),
            ("obj/Handler.cs", """
                namespace Ext.Obj;

                class ObjHandler : ICapabilityHandler { }
                """));

        var handlers = ImplantExtensionOverlay.DiscoverHandlers(dir);

        var handler = Assert.Single(handlers);
        Assert.Equal(new ExtensionHandlerType("Ext", "OneHandler"), handler);
    }

    [Fact]
    public void RenderRegistrations_InstantiatesEachHandlerFullyQualified()
    {
        // The generated file replaces the checked-in stub with the same static
        // Handlers shape the beacon wires, one fully-qualified new per handler.
        // A global-namespace class needs the explicit global:: prefix -- the
        // generated file itself sits inside Rod.Implant.Internal.
        var rendered = ImplantExtensionOverlay.RenderRegistrations(new[]
        {
            new ExtensionHandlerType("MyTradecraft.Demo", "DemoPingHandler"),
            new ExtensionHandlerType("", "BareHandler"),
        });

        Assert.Contains("namespace Rod.Implant.Internal;", rendered);
        Assert.Contains("internal static class ExtensionRegistrations", rendered);
        Assert.Contains("new MyTradecraft.Demo.DemoPingHandler(),", rendered);
        Assert.Contains("new global::BareHandler(),", rendered);
    }

    [Fact]
    public void Apply_CopiesSourcesAndReplacesTheRegistrationsStub()
    {
        // The overlay drops the extension's sources into the staging copy's
        // Extensions/ folder and overwrites the stub with the generated
        // registrations, so the implant tree is never touched and every
        // implant-class build carries the handlers.
        var dir = WriteExtension("apply", ("Handler.cs", """
            namespace Ext;

            internal sealed class OneHandler : ICapabilityHandler { }
            """));
        var staging = Path.Combine(_root, "staging");
        var extensions = Path.Combine(staging, "Extensions");
        Directory.CreateDirectory(extensions);
        File.WriteAllText(
            Path.Combine(extensions, ImplantExtensionOverlay.RegistrationsFileName),
            "// the checked-in dev stub, compiling empty");

        ImplantExtensionOverlay.Apply(dir, staging);

        Assert.True(File.Exists(Path.Combine(extensions, "Handler.cs")));
        var registrations = File.ReadAllText(Path.Combine(extensions, ImplantExtensionOverlay.RegistrationsFileName));
        Assert.Contains("new Ext.OneHandler(),", registrations);
        Assert.DoesNotContain("the checked-in dev stub", registrations);
    }

    [Fact]
    public void Apply_AMissingDirectoryFailsLoudly()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var staging = Path.Combine(_root, "staging-missing");
        Directory.CreateDirectory(staging);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ImplantExtensionOverlay.Apply(missing, staging));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Apply_NoDiscoveredHandlerFailsLoudly()
    {
        // A configured directory with no handler is a convention mismatch or a
        // wrong path -- both states an operator must hear about at build time,
        // not discover as an "unknown verb" failure on the target.
        var dir = WriteExtension("no-handler", ("Helpers.cs", """
            namespace Ext;

            internal static class OnlyHelpers { }
            """));
        var staging = Path.Combine(_root, "staging-none");
        Directory.CreateDirectory(staging);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ImplantExtensionOverlay.Apply(dir, staging));
        Assert.Contains("ICapabilityHandler", ex.Message);
    }

    [Fact]
    public void Discover_ReadsTheVerbLiteral()
    {
        // The verb each handler serves, read from the expression-bodied Verb
        // declaration the authoring shape documents: it is what the bake-time
        // handler trim classifies the handler by. A shape the scan cannot
        // read (a block-bodied property, several literals in one span) reads
        // as null and never drops.
        var dir = WriteExtension("verbs",
            ("Keylog.cs", """
                namespace Kit.Collect;

                internal sealed class KeylogHandler : ICapabilityHandler
                {
                    public string Verb => "collect.keylog";
                    public HandlerResult Handle(string arguments) => (TaskOutcome.Failed, "stand-in");
                }
                """),
            ("BlockBodied.cs", """
                namespace Kit.Odd;

                internal sealed class BlockBodiedHandler : ICapabilityHandler
                {
                    public string Verb { get { return "demo.block"; } }
                    public HandlerResult Handle(string arguments) => (TaskOutcome.Succeeded, "ack");
                }
                """));

        var handlers = ImplantExtensionOverlay.Discover(dir);

        Assert.Equal(
            new[]
            {
                new ExtensionHandler(
                    new ExtensionHandlerType("Kit.Odd", "BlockBodiedHandler"), null, "BlockBodied.cs"),
                new ExtensionHandler(
                    new ExtensionHandlerType("Kit.Collect", "KeylogHandler"), "collect.keylog", "Keylog.cs"),
            },
            handlers);
    }

    [Fact]
    public void Apply_WithheldVerb_StaysOutOfTheStagingTree()
    {
        // The handler trim's extension leg, against the verb the acceptance
        // names: keylogging is gated to the stage-2 class, so a pivot build
        // compiles neither the keylog handler's source nor its registration,
        // while a verb no class lists (the operator's own) and the plain
        // helper sources ride along as before.
        var dir = WriteExtension("trimmed",
            ("Keylog.cs", """
                namespace Kit.Collect;

                internal sealed class KeylogHandler : ICapabilityHandler
                {
                    public string Verb => "collect.keylog";
                    public HandlerResult Handle(string arguments) => (TaskOutcome.Failed, "stand-in");
                }
                """),
            ("DemoPing.cs", """
                namespace Kit.Demo;

                internal sealed class DemoPingHandler : ICapabilityHandler
                {
                    public string Verb => "demo.ping";
                    public HandlerResult Handle(string arguments) => (TaskOutcome.Succeeded, "pong");
                }
                """),
            ("Helpers.cs", """
                namespace Kit;

                internal static class KitHelpers { }
                """));
        var staging = Path.Combine(_root, "staging-trim");
        var extensions = Path.Combine(staging, "Extensions");
        Directory.CreateDirectory(extensions);

        ImplantExtensionOverlay.Apply(
            dir, staging, verb => HandlerModuleSelection.CompilesVerb(ImplantClass.Pivot, verb));

        Assert.False(File.Exists(Path.Combine(extensions, "Keylog.cs")));
        Assert.True(File.Exists(Path.Combine(extensions, "DemoPing.cs")));
        Assert.True(File.Exists(Path.Combine(extensions, "Helpers.cs")));
        var registrations = File.ReadAllText(Path.Combine(extensions, ImplantExtensionOverlay.RegistrationsFileName));
        Assert.Contains("new Kit.Demo.DemoPingHandler(),", registrations);
        Assert.DoesNotContain("KeylogHandler", registrations);
    }

    [Fact]
    public void Apply_AnUnreadableVerbShape_RidesEveryBuild()
    {
        // The conservative leg: a handler whose verb the scan cannot read
        // compiles into every build even under a predicate that withholds
        // everything -- the trim never silently drops what it cannot
        // classify.
        var dir = WriteExtension("unreadable", ("BlockBodied.cs", """
            namespace Kit.Odd;

            internal sealed class BlockBodiedHandler : ICapabilityHandler
            {
                public string Verb { get { return "demo.block"; } }
                public HandlerResult Handle(string arguments) => (TaskOutcome.Succeeded, "ack");
            }
            """));
        var staging = Path.Combine(_root, "staging-unreadable");
        var extensions = Path.Combine(staging, "Extensions");
        Directory.CreateDirectory(extensions);

        ImplantExtensionOverlay.Apply(dir, staging, verbCompiles: _ => false);

        Assert.True(File.Exists(Path.Combine(extensions, "BlockBodied.cs")));
        var registrations = File.ReadAllText(Path.Combine(extensions, ImplantExtensionOverlay.RegistrationsFileName));
        Assert.Contains("new Kit.Odd.BlockBodiedHandler(),", registrations);
    }
}
