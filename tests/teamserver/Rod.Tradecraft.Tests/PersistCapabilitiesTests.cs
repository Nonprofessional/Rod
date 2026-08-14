using Rod.Tradecraft;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Evasion;
using Rod.Tradecraft.Lateral;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Persist;
using Rod.Tradecraft.Recon;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// Roadmap  acceptance at the contract layer: the persistence verbs
/// (architecture.md Sec 10.1) load through the tradecraft registry alongside the
/// core, recon, and lateral sets, are listed in the Persist category, register as placeholders (their concrete behavior is out-of-tree, like
/// the non-shell core verbs and the recon and lateral verbs), carry their OPSEC
/// attributes, and respect the same out-of-tree-override rule.
/// </summary>
public class PersistCapabilitiesTests
{
    [Fact]
    public async Task DefaultRegistry_ListsEveryPersistVerb()
    {
        // The persist verbs load through the registry: all three appear, each in
        // the persist category, after the default registry is built.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var descriptors = await registry.ListAsync();

        var byVerb = descriptors.ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);
        foreach (var verb in PersistCapabilities.Verbs)
        {
            Assert.True(byVerb.ContainsKey(verb), $"persist verb '{verb}' is not registered");
            Assert.Equal(CapabilityCategory.Persist, byVerb[verb].Category);
        }
    }

    [Fact]
    public async Task DefaultRegistry_FlagsPersistVerbs_InAttributes()
    {
        // Each state-changing persist verb carries the OPSEC attribute for what it
        // touches (architecture.md Sec 7): persist.install writes to disk and
        // establishes a foothold, persist.remove writes to disk; persist.list is
        // a read and carries no such flag, like the host-local recon.hostenum.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var descriptors = (await registry.ListAsync())
            .ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);

        Assert.True(descriptors[PersistCapabilities.Install].Attributes.TryGetValue("writes-to-disk", out var instDisk));
        Assert.Equal("true", instDisk);
        Assert.True(descriptors[PersistCapabilities.Install].Attributes.TryGetValue("persists", out var instPersist));
        Assert.Equal("true", instPersist);
        Assert.True(descriptors[PersistCapabilities.Remove].Attributes.TryGetValue("writes-to-disk", out var rmDisk));
        Assert.Equal("true", rmDisk);
        Assert.False(descriptors[PersistCapabilities.Remove].Attributes.ContainsKey("persists"));
        Assert.False(descriptors[PersistCapabilities.List].Attributes.ContainsKey("writes-to-disk"));
        Assert.False(descriptors[PersistCapabilities.List].Attributes.ContainsKey("persists"));
    }

    [Fact]
    public async Task DefaultRegistry_RegistersThePersistVerbs_AsPlaceholders()
    {
        // Concrete persistence behavior is out-of-tree (architecture.md Sec 13,
        // AGENTS.md Sec 7): the verbs register as placeholders only -- the
        // registry lists them and the task gate admits them, while execution
        // lives on the implant (architecture.md Sec 5.3, Sec 10.2/10.3).
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var found = await registry.FindAsync(PersistCapabilities.List);
        Assert.IsType<PlaceholderCapabilityModule>(found);
    }

    [Fact]
    public async Task DefaultRegistry_ListsCoreReconLateralAndPersistSets()
    {
        // The default registry is the union of the core, recon, lateral, and
        // persist sets: every verb in each set is present, so the operator-visible
        // capability surface is the full built-in set.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();

        foreach (var verb in CoreCapabilities.Verbs)
            Assert.Contains(verb, verbs);
        foreach (var verb in ReconCapabilities.Verbs)
            Assert.Contains(verb, verbs);
        foreach (var verb in LateralCapabilities.Verbs)
            Assert.Contains(verb, verbs);
        foreach (var verb in PersistCapabilities.Verbs)
            Assert.Contains(verb, verbs);
        foreach (var verb in EvasionCapabilities.Verbs)
            Assert.Contains(verb, verbs);
    }

    [Fact]
    public async Task LoadCapabilities_LeavesCallerPersistOverrideInPlace()
    {
        // An out-of-tree persist module registered before the built-in load must
        // stay the authority for its verb: the loader deduplicates against what
        // the registry already holds, the same rule that protects core, recon,
        // and lateral overrides.
        var registry = new InMemoryCapabilityRegistry();
        var overrideModule = new FixedModule("persist.install");
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync("persist.install");
        Assert.Same(overrideModule, found);
        // The other persist verbs still load alongside the override.
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();
        Assert.Contains(PersistCapabilities.Remove, verbs);
    }

    // A module whose descriptor is fixed at construction, so a test can stand in
    // for an out-of-tree override without writing real tradecraft.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }

        public FixedModule(string verb)
            => Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Persist, "1.0");
    }
}
