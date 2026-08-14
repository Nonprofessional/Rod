using Rod.Tradecraft;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// The  acceptance point: a stub module registers and is
/// dispatched, and the core verbs load through the tradecraft layer
/// (architecture.md Sec 10.1). <see cref="RodTradecraftHost.BuildDefaultRegistryAsync"/>
/// wires an in-memory registry preloaded with the core verbs, and dispatching
/// <c>shell.exec</c> against it round-trips through the stub module.
/// </summary>
public class CoreCapabilitiesTests
{
    [Fact]
    public async Task DefaultRegistry_ListsEveryCoreVerb()
    {
        // The core verbs load through the registry: all five appear, each in
        // the core category, after the default registry is built.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var descriptors = await registry.ListAsync();

        var byVerb = descriptors.ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);
        foreach (var verb in CoreCapabilities.Verbs)
        {
            Assert.True(byVerb.ContainsKey(verb), $"core verb '{verb}' is not registered");
            Assert.Equal(CapabilityCategory.Core, byVerb[verb].Category);
        }
    }

    [Fact]
    public async Task DefaultRegistry_DispatchesShellExecThroughTheStub()
    {
        // The  acceptance literal: a stub module registers and is
        // dispatched. shell.exec is the dispatchable core verb; the stub echoes
        // its arguments so the round-trip is observable.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var dispatcher = new CapabilityDispatcher(registry);

        var result = await dispatcher.DispatchAsync(
            new CapabilityInvocation(CoreCapabilities.ShellExec, "uname -a"));

        Assert.Equal(CapabilityStatus.Succeeded, result.Status);
        Assert.Contains("shell.exec stub", result.Output);
        Assert.Contains("uname -a", result.Output);
    }

    [Fact]
    public async Task DefaultRegistry_OtherCoreVerbs_AreRegisteredButNotImplemented()
    {
        // The remaining core verbs are registered (so the registry lists them)
        // but have no in-process implementation: concrete behavior runs on the
        // implant and is not part of this repository (architecture.md Sec 13).
        // Dispatch therefore reports a failure, not a NotFound -- the verb is
        // known, just unimplemented in-process.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var dispatcher = new CapabilityDispatcher(registry);

        var result = await dispatcher.DispatchAsync(
            new CapabilityInvocation(CoreCapabilities.FilePush, "/etc/passwd"));

        Assert.Equal(CapabilityStatus.Failed, result.Status);
        Assert.Contains(CoreCapabilities.FilePush, result.Error ?? string.Empty);
    }

    [Fact]
    public async Task LoadCoreCapabilities_LeavesCallerOverrideInPlace()
    {
        // An out-of-tree module registered before the core load must stay the
        // authority for its verb: the core loader deduplicates against what the
        // registry already holds rather than overwriting it with a placeholder.
        var registry = new InMemoryCapabilityRegistry();
        var overrideModule = new FixedModule("shell.exec", "real implementation");
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync("shell.exec");
        Assert.Same(overrideModule, found);
        // The other core verbs still load alongside the override.
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();
        Assert.Contains(CoreCapabilities.FilePull, verbs);
    }

    // A module whose result is fixed at construction, so a test can stand in for
    // an out-of-tree override without writing real tradecraft.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }
        private readonly string _output;

        public FixedModule(string verb, string output)
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
