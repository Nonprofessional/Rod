using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// Registry behavior for the tradecraft layer skeleton (roadmap M2.5,
/// architecture.md Sec 10). <see cref="InMemoryCapabilityRegistry"/> indexes
/// modules by their descriptor's verb (case-insensitive), lists descriptors in
/// registration order, and lets a later registration replace an earlier one so
/// an out-of-tree module loaded after the core stubs becomes the authority for
/// its verb.
/// </summary>
public class CapabilityRegistryTests
{
    [Fact]
    public async Task Register_ThenFind_ReturnsTheModule()
    {
        var registry = new InMemoryCapabilityRegistry();
        var module = new CoreCapabilityModule();

        await registry.RegisterAsync(module);

        var found = await registry.FindAsync(CoreCapabilities.ShellExec);
        Assert.Same(module, found);
    }

    [Fact]
    public async Task Find_UnknownVerb_ReturnsNull()
    {
        var registry = new InMemoryCapabilityRegistry();

        var found = await registry.FindAsync("does.not.exist");

        Assert.Null(found);
    }

    [Fact]
    public async Task Find_IsCaseInsensitive()
    {
        // Capability verbs are namespace.action identifiers, not display
        // strings; a caller must not be told the verb is unknown because of
        // casing.
        var registry = new InMemoryCapabilityRegistry();
        await registry.RegisterAsync(new CoreCapabilityModule());

        var found = await registry.FindAsync("SHELL.EXEC");

        Assert.NotNull(found);
        Assert.Equal(CoreCapabilities.ShellExec, found!.Descriptor.Verb);
    }

    [Fact]
    public async Task List_ReflectsRegistrationOrder()
    {
        var registry = new InMemoryCapabilityRegistry();
        var first = new Stub("recon.portscan");
        var second = new Stub("lateral.move");
        var third = new Stub("persist.install");

        await registry.RegisterAsync(first);
        await registry.RegisterAsync(second);
        await registry.RegisterAsync(third);

        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();
        Assert.Equal(new[] { "recon.portscan", "lateral.move", "persist.install" }, verbs);
    }

    [Fact]
    public async Task Register_ForAnExistingVerb_ReplacesIt()
    {
        // An out-of-tree module loaded after the core placeholder must win for
        // its verb: the last registration is the single authority.
        var registry = new InMemoryCapabilityRegistry();
        var placeholder = new Stub("shell.exec", output: "placeholder");
        var real = new Stub("shell.exec", output: "real");

        await registry.RegisterAsync(placeholder);
        await registry.RegisterAsync(real);

        var found = await registry.FindAsync("shell.exec");
        Assert.Same(real, found);
        // The list is deduplicated by verb, so the replaced verb appears once.
        var listed = (await registry.ListAsync()).Where(d => d.Verb == "shell.exec").ToArray();
        Assert.Single(listed);
    }

    // A tiny module whose descriptor and result are fixed at construction, so the
    // tests above can register arbitrary verbs without pulling in real tradecraft.
    private sealed class Stub : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }
        private readonly string _output;

        public Stub(string verb, string output = "")
        {
            Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Core, "1.0");
            _output = output;
        }

        public Task<CapabilityResult> ExecuteAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CapabilityResult.Succeeded(_output));
    }
}
