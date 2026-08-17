using Rod.CoreState.Implants;
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
/// Contract-layer acceptance: the evasion verbs
/// (architecture.md Sec 10.1, Sec 10.2) load through the tradecraft registry
/// alongside the core, recon, lateral, persist, collect, and exfil sets, are
/// listed in the Evasion category, register as placeholders
/// (their concrete behavior is out-of-tree, like the non-shell core verbs and the
/// recon, lateral, persist, collect, and exfil verbs), carry their OPSEC
/// attributes, and respect the same out-of-tree-override rule.
/// </summary>
/// <remarks>
/// These tests are the acceptance criteria in code: an out-of-tree module
/// that registers for an evasion verb is the authority for it and is dispatched
/// through the contract. Evasion is a sensitive category (architecture.md Sec 13,
/// RESPONSIBLE-USE.md): the core ships no concrete behavior, only the contract,
/// registration, and dispatch exercised here.
/// </remarks>
public class EvasionCapabilitiesTests
{
    [Fact]
    public async Task DefaultRegistry_ListsEveryEvasionVerb()
    {
        // The evasion verbs load through the registry: both appear, each in the
        // evasion category, after the default registry is built.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var descriptors = await registry.ListAsync();

        var byVerb = descriptors.ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);
        foreach (var verb in EvasionCapabilities.Verbs)
        {
            Assert.True(byVerb.ContainsKey(verb), $"evasion verb '{verb}' is not registered");
            Assert.Equal(CapabilityCategory.Evasion, byVerb[verb].Category);
        }
    }

    [Fact]
    public async Task DefaultRegistry_FlagsEvasionVerbs_AsModifyingDefenses()
    {
        // Each evasion verb alters the target's defensive or monitoring posture,
        // so both carry the modifies-defenses OPSEC attribute (architecture.md
        // Sec 7), like the state-changing persist, collect, and lateral verbs
        // carry the attribute describing what they touch.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var descriptors = (await registry.ListAsync())
            .ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);

        foreach (var verb in EvasionCapabilities.Verbs)
        {
            Assert.True(descriptors[verb].Attributes.TryGetValue("modifies-defenses", out var flag),
                $"evasion verb '{verb}' is missing the modifies-defenses attribute");
            Assert.Equal("true", flag);
        }
    }

    [Fact]
    public async Task DefaultRegistry_RegistersTheEvasionVerbs_AsPlaceholders()
    {
        // Concrete evasion behavior is out-of-tree (architecture.md Sec 13,
        // AGENTS.md Sec 7): the verbs register as placeholders only -- the
        // registry lists them and the task gate admits them, while execution
        // lives on the implant (architecture.md Sec 5.3, Sec 10.2/10.3).
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var found = await registry.FindAsync(EvasionCapabilities.Avoid);
        Assert.IsType<PlaceholderCapabilityModule>(found);
    }

    [Fact]
    public async Task DefaultRegistry_ListsAllBuiltInSets()
    {
        // The default registry is the union of the core, recon, lateral,
        // persist, collect, exfil, and evasion sets: every verb in each set is
        // present, so the operator-visible capability surface is the full
        // built-in set.
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
    public async Task LoadCapabilities_LeavesCallerEvasionOverrideInPlace()
    {
        // An out-of-tree evasion module registered before the built-in load must
        // stay the authority for its verb: the loader deduplicates against what
        // the registry already holds, the same rule that protects core, recon,
        // lateral, persist, collect, and exfil overrides. This is the
        // acceptance criterion: an out-of-tree module registers and replaces the
        // placeholder through the contract.
        var registry = new InMemoryCapabilityRegistry();
        var overrideModule = new FixedModule("evasion.avoid");
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync("evasion.avoid");
        Assert.Same(overrideModule, found);
        // The other evasion verb still loads alongside the override.
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();
        Assert.Contains(EvasionCapabilities.Unload, verbs);

        // And the override stays the authority end to end: the registry-backed
        // task resolver admits the verb because the out-of-tree module is what
        // is registered, not the placeholder.
        var resolver = new CapabilityRegistryTaskResolver(registry);
        Assert.True(resolver.IsDispatchable(ImplantClass.Stage2, "evasion.avoid"));
    }

    // A module whose descriptor is fixed at construction, so a test can stand in
    // for an out-of-tree override without writing real tradecraft.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }

        public FixedModule(string verb)
            => Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Evasion, "1.0");
    }
}
