using Rod.Tradecraft;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// The core-verb registration surface (architecture.md Sec 10.1):
/// <see cref="RodTradecraftHost.BuildDefaultRegistryAsync"/> wires an in-memory
/// registry preloaded with the core verbs, and every verb registers as a
/// placeholder -- the teamserver gates and forwards only, so no core verb has an
/// in-process implementation (architecture.md Sec 10.2/10.3).
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
    public async Task DefaultRegistry_RegistersCoreVerbsAsPlaceholders()
    {
        // Every core verb, shell.exec included, registers as a placeholder: the
        // registry holds the declaration only, and execution lives on the
        // implant (architecture.md Sec 5.3, Sec 10.2/10.3).
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var found = await registry.FindAsync(CoreCapabilities.ShellExec);
        Assert.IsType<PlaceholderCapabilityModule>(found);
    }

    [Fact]
    public async Task LoadCoreCapabilities_LeavesCallerOverrideInPlace()
    {
        // An out-of-tree module registered before the core load must stay the
        // authority for its verb: the core loader deduplicates against what the
        // registry already holds rather than overwriting it with a placeholder.
        var registry = new InMemoryCapabilityRegistry();
        var overrideModule = new FixedModule("shell.exec");
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync("shell.exec");
        Assert.Same(overrideModule, found);
        // The other core verbs still load alongside the override.
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();
        Assert.Contains(CoreCapabilities.FilePull, verbs);
    }

    // A module whose descriptor is fixed at construction, so a test can stand in
    // for an out-of-tree override without writing real tradecraft.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }

        public FixedModule(string verb)
            => Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Core, "1.0");
    }
}
