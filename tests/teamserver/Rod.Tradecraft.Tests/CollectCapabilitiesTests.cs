using Rod.Tradecraft;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Collect;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Evasion;
using Rod.Tradecraft.Exfil;
using Rod.Tradecraft.Lateral;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Persist;
using Rod.Tradecraft.Recon;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// Contract-layer acceptance: the collection verbs
/// (architecture.md Sec 10.1) load through the tradecraft registry alongside the
/// core, recon, lateral, persist, and exfil sets, are listed in the Collect
/// category, register as placeholders (server-side execution is out-of-tree by
/// contract; the reference implant runs the verbs on the target), carry their
/// OPSEC attributes, and respect the same out-of-tree-override rule.
/// </summary>
public class CollectCapabilitiesTests
{
    [Fact]
    public async Task DefaultRegistry_ListsEveryCollectVerb()
    {
        // The collect verbs load through the registry: all three appear, each in
        // the collect category, after the default registry is built.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var descriptors = await registry.ListAsync();

        var byVerb = descriptors.ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);
        foreach (var verb in CollectCapabilities.Verbs)
        {
            Assert.True(byVerb.ContainsKey(verb), $"collect verb '{verb}' is not registered");
            Assert.Equal(CapabilityCategory.Collect, byVerb[verb].Category);
        }
    }

    [Fact]
    public async Task DefaultRegistry_FlagsCollectVerbs_InAttributes()
    {
        // Each collect verb carries the OPSEC attribute for what it reads
        // (architecture.md Sec 7): collect.cred reads a credential, and
        // collect.keylog installs a resident input-capture hook so it both reads
        // input and persists on the target.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var descriptors = (await registry.ListAsync())
            .ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);

        Assert.True(descriptors[CollectCapabilities.Cred].Attributes.TryGetValue("reads-credential", out var credCred));
        Assert.Equal("true", credCred);
        Assert.True(descriptors[CollectCapabilities.Keylog].Attributes.TryGetValue("reads-input", out var keylogInput));
        Assert.Equal("true", keylogInput);
        Assert.True(descriptors[CollectCapabilities.Keylog].Attributes.TryGetValue("persists", out var keylogPersists));
        Assert.Equal("true", keylogPersists);
        // A credential listing installs no resident hook (keylog is the one that persists).
        Assert.False(descriptors[CollectCapabilities.Cred].Attributes.ContainsKey("persists"));
    }

    [Fact]
    public async Task DefaultRegistry_RegistersTheCollectVerbs_AsPlaceholders()
    {
        // Concrete collection behavior is out-of-tree (architecture.md Sec 13,
        // AGENTS.md Sec 7): the verbs register as placeholders only -- the
        // registry lists them and the task gate admits them, while execution
        // lives on the implant (architecture.md Sec 5.3, Sec 10.2/10.3).
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var found = await registry.FindAsync(CollectCapabilities.Cred);
        Assert.IsType<PlaceholderCapabilityModule>(found);
    }

    [Fact]
    public async Task DefaultRegistry_ListsCoreReconLateralPersistCollectAndExfilSets()
    {
        // The default registry is the union of the core, recon, lateral,
        // persist, collect, and exfil sets: every verb in each set is present,
        // so the operator-visible capability surface is the full built-in set.
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
        foreach (var verb in CollectCapabilities.Verbs)
            Assert.Contains(verb, verbs);
        foreach (var verb in ExfilCapabilities.Verbs)
            Assert.Contains(verb, verbs);
        foreach (var verb in EvasionCapabilities.Verbs)
            Assert.Contains(verb, verbs);
    }

    [Fact]
    public async Task LoadCapabilities_LeavesCallerCollectOverrideInPlace()
    {
        // An out-of-tree collect module registered before the built-in load must
        // stay the authority for its verb: the loader deduplicates against what
        // the registry already holds, the same rule that protects core, recon,
        // lateral, and persist overrides.
        var registry = new InMemoryCapabilityRegistry();
        var overrideModule = new FixedModule("collect.cred");
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync("collect.cred");
        Assert.Same(overrideModule, found);
        // The other collect verbs still load alongside the override.
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();
        Assert.Contains(CollectCapabilities.Cred, verbs);
    }

    // A module whose descriptor is fixed at construction, so a test can stand in
    // for an out-of-tree override without writing real tradecraft.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }

        public FixedModule(string verb)
            => Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Collect, "1.0");
    }
}
