using Rod.Tradecraft;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Evasion;
using Rod.Tradecraft.Lateral;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Recon;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// Roadmap  acceptance at the contract layer: the lateral-movement verbs
/// (architecture.md Sec 10.1) load through the tradecraft registry alongside the
/// core and recon sets, are listed in the Lateral category, dispatch as
/// registered-but-not-implemented (their concrete behavior is out-of-tree, like
/// the non-shell core verbs and the recon verbs), carry their OPSEC attributes,
/// and respect the same out-of-tree-override rule.
/// </summary>
public class LateralCapabilitiesTests
{
    [Fact]
    public async Task DefaultRegistry_ListsEveryLateralVerb()
    {
        // The lateral verbs load through the registry: all three appear, each in
        // the lateral category, after the default registry is built.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var descriptors = await registry.ListAsync();

        var byVerb = descriptors.ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);
        foreach (var verb in LateralCapabilities.Verbs)
        {
            Assert.True(byVerb.ContainsKey(verb), $"lateral verb '{verb}' is not registered");
            Assert.Equal(CapabilityCategory.Lateral, byVerb[verb].Category);
        }
    }

    [Fact]
    public async Task DefaultRegistry_FlagsLateralVerbs_InAttributes()
    {
        // Each lateral verb carries the OPSEC attribute for what it touches
        // (architecture.md Sec 7): lateral.move derives a child, lateral.token
        // handles a credential, lateral.exec_remote touches the network.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var descriptors = (await registry.ListAsync())
            .ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);

        Assert.True(descriptors[LateralCapabilities.Move].Attributes.TryGetValue("derives-child", out var mv));
        Assert.Equal("true", mv);
        Assert.True(descriptors[LateralCapabilities.Token].Attributes.TryGetValue("touches-credential", out var tk));
        Assert.Equal("true", tk);
        Assert.True(descriptors[LateralCapabilities.ExecRemote].Attributes.TryGetValue("touches-network", out var er));
        Assert.Equal("true", er);
    }

    [Fact]
    public async Task DefaultRegistry_DispatchesALateralVerb_AsRegisteredButNotImplemented()
    {
        // Concrete lateral-movement behavior is out-of-tree (architecture.md
        // Sec 13, AGENTS.md Sec 7), so dispatching a lateral verb against the
        // default registry reports a failure -- the verb is known, just
        // unimplemented in-process -- the same outcome the non-shell core verbs
        // and the recon verbs produce.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var dispatcher = new CapabilityDispatcher(registry);

        var result = await dispatcher.DispatchAsync(
            new CapabilityInvocation(LateralCapabilities.Move, "stage2 ./child"));

        Assert.Equal(CapabilityStatus.Failed, result.Status);
        Assert.Contains(LateralCapabilities.Move, result.Error ?? string.Empty);
    }

    [Fact]
    public async Task DefaultRegistry_ListsCoreReconAndLateralSets()
    {
        // The default registry is the union of the core, recon, and lateral
        // sets: every verb in each set is present, so the operator-visible
        // capability surface is the full built-in set.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();

        foreach (var verb in CoreCapabilities.Verbs)
            Assert.Contains(verb, verbs);
        foreach (var verb in ReconCapabilities.Verbs)
            Assert.Contains(verb, verbs);
        foreach (var verb in LateralCapabilities.Verbs)
            Assert.Contains(verb, verbs);
        foreach (var verb in EvasionCapabilities.Verbs)
            Assert.Contains(verb, verbs);
    }

    [Fact]
    public async Task LoadCapabilities_LeavesCallerLateralOverrideInPlace()
    {
        // An out-of-tree lateral module registered before the built-in load must
        // stay the authority for its verb: the loader deduplicates against what
        // the registry already holds, the same rule that protects core and recon
        // overrides.
        var registry = new InMemoryCapabilityRegistry();
        var overrideModule = new FixedModule("lateral.move", "real lateral module");
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync("lateral.move");
        Assert.Same(overrideModule, found);
        // The other lateral verbs still load alongside the override.
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();
        Assert.Contains(LateralCapabilities.Token, verbs);
    }

    // A module whose result is fixed at construction, so a test can stand in for
    // an out-of-tree override without writing real tradecraft. Mirrors the helper
    // in ReconCapabilitiesTests.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }
        private readonly string _output;

        public FixedModule(string verb, string output)
        {
            Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Lateral, "1.0");
            _output = output;
        }

        public Task<CapabilityResult> ExecuteAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CapabilityResult.Succeeded(_output));
    }
}
