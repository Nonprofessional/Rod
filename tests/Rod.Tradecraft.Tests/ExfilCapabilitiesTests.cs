using Rod.Tradecraft;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Collect;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Exfil;
using Rod.Tradecraft.Lateral;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Persist;
using Rod.Tradecraft.Recon;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// Roadmap M5.4 acceptance at the contract layer: the exfiltration verbs
/// (architecture.md Sec 10.1) load through the tradecraft registry alongside the
/// core, recon, lateral, persist, and collect sets, are listed in the Exfil
/// category, dispatch as registered-but-not-implemented (their concrete behavior
/// is out-of-tree, like the non-shell core verbs and the recon, lateral, and
/// persist verbs), carry their OPSEC attributes, and respect the same
/// out-of-tree-override rule.
/// </summary>
public class ExfilCapabilitiesTests
{
    [Fact]
    public async Task DefaultRegistry_ListsEveryExfilVerb()
    {
        // The exfil verbs load through the registry: both appear, each in the
        // exfil category, after the default registry is built.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var descriptors = await registry.ListAsync();

        var byVerb = descriptors.ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);
        foreach (var verb in ExfilCapabilities.Verbs)
        {
            Assert.True(byVerb.ContainsKey(verb), $"exfil verb '{verb}' is not registered");
            Assert.Equal(CapabilityCategory.Exfil, byVerb[verb].Category);
        }
    }

    [Fact]
    public async Task DefaultRegistry_FlagsExfilPush_AsNetworkTouching()
    {
        // exfil.push transfers data over the C2 channel and so carries the
        // touches-network OPSEC attribute (architecture.md Sec 7), like the
        // network-touching recon and lateral verbs; exfil.stage stages
        // already-collected data on the teamserver and touches neither the
        // target's network nor its disk, so it carries no such flag, like the
        // read-only persist.list and the host-local recon.hostenum.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var descriptors = (await registry.ListAsync())
            .ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);

        Assert.True(descriptors[ExfilCapabilities.Push].Attributes.TryGetValue("touches-network", out var pushNet));
        Assert.Equal("true", pushNet);
        Assert.False(descriptors[ExfilCapabilities.Stage].Attributes.ContainsKey("touches-network"));
    }

    [Fact]
    public async Task DefaultRegistry_DispatchesAnExfilVerb_AsRegisteredButNotImplemented()
    {
        // Concrete exfiltration behavior is out-of-tree (architecture.md Sec 13,
        // AGENTS.md Sec 7), so dispatching an exfil verb against the default
        // registry reports a failure -- the verb is known, just unimplemented
        // in-process -- the same outcome the non-shell core verbs and the recon,
        // lateral, and persist verbs produce.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var dispatcher = new CapabilityDispatcher(registry);

        var result = await dispatcher.DispatchAsync(
            new CapabilityInvocation(ExfilCapabilities.Push, ""));

        Assert.Equal(CapabilityStatus.Failed, result.Status);
        Assert.Contains(ExfilCapabilities.Push, result.Error ?? string.Empty);
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
    }

    [Fact]
    public async Task LoadCapabilities_LeavesCallerExfilOverrideInPlace()
    {
        // An out-of-tree exfil module registered before the built-in load must
        // stay the authority for its verb: the loader deduplicates against what
        // the registry already holds, the same rule that protects core, recon,
        // lateral, persist, and collect overrides.
        var registry = new InMemoryCapabilityRegistry();
        var overrideModule = new FixedModule("exfil.push", "real exfil module");
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync("exfil.push");
        Assert.Same(overrideModule, found);
        // The other exfil verb still loads alongside the override.
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();
        Assert.Contains(ExfilCapabilities.Stage, verbs);
    }

    // A module whose result is fixed at construction, so a test can stand in for
    // an out-of-tree override without writing real tradecraft. Mirrors the helper
    // in PersistCapabilitiesTests.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }
        private readonly string _output;

        public FixedModule(string verb, string output)
        {
            Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Exfil, "1.0");
            _output = output;
        }

        public Task<CapabilityResult> ExecuteAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CapabilityResult.Succeeded(_output));
    }
}
