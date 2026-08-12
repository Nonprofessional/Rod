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
/// Roadmap M7.1 acceptance at the contract layer: the evasion verbs
/// (architecture.md Sec 10.1, Sec 10.2) load through the tradecraft registry
/// alongside the core, recon, lateral, persist, collect, and exfil sets, are
/// listed in the Evasion category, dispatch as registered-but-not-implemented
/// (their concrete behavior is out-of-tree, like the non-shell core verbs and the
/// recon, lateral, persist, collect, and exfil verbs), carry their OPSEC
/// attributes, and respect the same out-of-tree-override rule.
/// </summary>
/// <remarks>
/// These tests are the M7.1 acceptance criteria in code: an out-of-tree module
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
    public async Task DefaultRegistry_DispatchesAnEvasionVerb_AsRegisteredButNotImplemented()
    {
        // Concrete evasion behavior is out-of-tree (architecture.md Sec 10.2,
        // Sec 13, RESPONSIBLE-USE.md, AGENTS.md Sec 7), so dispatching an evasion
        // verb against the default registry reports a failure -- the verb is
        // known, just unimplemented in-process -- the same outcome the non-shell
        // core verbs and the recon, lateral, persist, collect, and exfil verbs
        // produce.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var dispatcher = new CapabilityDispatcher(registry);

        var result = await dispatcher.DispatchAsync(
            new CapabilityInvocation(EvasionCapabilities.Avoid, ""));

        Assert.Equal(CapabilityStatus.Failed, result.Status);
        Assert.Contains(EvasionCapabilities.Avoid, result.Error ?? string.Empty);
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
        // lateral, persist, collect, and exfil overrides. This is the M7.1
        // acceptance criterion: an out-of-tree module registers and dispatches
        // through the contract.
        var registry = new InMemoryCapabilityRegistry();
        var overrideModule = new FixedModule("evasion.avoid", "real evasion module");
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync("evasion.avoid");
        Assert.Same(overrideModule, found);
        // The other evasion verb still loads alongside the override.
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();
        Assert.Contains(EvasionCapabilities.Unload, verbs);

        // And the override dispatches through the contract: the loader did not
        // replace it, so a dispatch reaches the out-of-tree module.
        var dispatcher = new CapabilityDispatcher(registry);
        var result = await dispatcher.DispatchAsync(
            new CapabilityInvocation("evasion.avoid", ""));
        Assert.Equal(CapabilityStatus.Succeeded, result.Status);
        Assert.Equal("real evasion module", result.Output);
    }

    // A module whose result is fixed at construction, so a test can stand in for
    // an out-of-tree override without writing real tradecraft. Mirrors the helper
    // in ExfilCapabilitiesTests.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }
        private readonly string _output;

        public FixedModule(string verb, string output)
        {
            Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Evasion, "1.0");
            _output = output;
        }

        public Task<CapabilityResult> ExecuteAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CapabilityResult.Succeeded(_output));
    }
}
