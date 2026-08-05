using Rod.Tradecraft;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Recon;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// Roadmap M5.1 acceptance at the contract layer: the recon verbs
/// (architecture.md Sec 10.1) load through the tradecraft registry alongside the
/// core set, are listed in the Recon category, dispatch as registered-but-not-
/// implemented (their concrete behavior is out-of-tree, like the non-shell core
/// verbs), and respect the same out-of-tree-override rule.
/// </summary>
public class ReconCapabilitiesTests
{
    [Fact]
    public async Task DefaultRegistry_ListsEveryReconVerb()
    {
        // The recon verbs load through the registry: all three appear, each in
        // the recon category, after the default registry is built.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var descriptors = await registry.ListAsync();

        var byVerb = descriptors.ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);
        foreach (var verb in ReconCapabilities.Verbs)
        {
            Assert.True(byVerb.ContainsKey(verb), $"recon verb '{verb}' is not registered");
            Assert.Equal(CapabilityCategory.Recon, byVerb[verb].Category);
        }
    }

    [Fact]
    public async Task DefaultRegistry_FlagsNetworkTouchingVerbs_InAttributes()
    {
        // The network-touching recon verbs carry a touches-network OPSEC
        // attribute so operators and tradecraft filters can surface them
        // (architecture.md Sec 7); hostenum introspects the local host and
        // omits it.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var descriptors = (await registry.ListAsync())
            .ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);

        Assert.True(descriptors[ReconCapabilities.PortScan].Attributes.TryGetValue("touches-network", out var ps));
        Assert.Equal("true", ps);
        Assert.True(descriptors[ReconCapabilities.Service].Attributes.TryGetValue("touches-network", out var sv));
        Assert.Equal("true", sv);
        Assert.False(descriptors[ReconCapabilities.HostEnum].Attributes.ContainsKey("touches-network"));
    }

    [Fact]
    public async Task DefaultRegistry_DispatchesAReconVerb_AsRegisteredButNotImplemented()
    {
        // Concrete recon behavior is out-of-tree (architecture.md Sec 13,
        // AGENTS.md Sec 7), so dispatching a recon verb against the default
        // registry reports a failure -- the verb is known, just unimplemented
        // in-process -- the same outcome the non-shell core verbs produce.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var dispatcher = new CapabilityDispatcher(registry);

        var result = await dispatcher.DispatchAsync(
            new CapabilityInvocation(ReconCapabilities.PortScan, "127.0.0.1 1-1024"));

        Assert.Equal(CapabilityStatus.Failed, result.Status);
        Assert.Contains(ReconCapabilities.PortScan, result.Error ?? string.Empty);
    }

    [Fact]
    public async Task DefaultRegistry_ListsBothCoreAndReconSets()
    {
        // The default registry is the union of the core and recon sets: every
        // core verb and every recon verb is present, so the operator-visible
        // capability surface is the full built-in set.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();

        foreach (var verb in CoreCapabilities.Verbs)
            Assert.Contains(verb, verbs);
        foreach (var verb in ReconCapabilities.Verbs)
            Assert.Contains(verb, verbs);
    }

    [Fact]
    public async Task LoadCapabilities_LeavesCallerReconOverrideInPlace()
    {
        // An out-of-tree recon module registered before the built-in load must
        // stay the authority for its verb: the loader deduplicates against what
        // the registry already holds, the same rule that protects core overrides.
        var registry = new InMemoryCapabilityRegistry();
        var overrideModule = new FixedModule("recon.portscan", "real recon module");
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync("recon.portscan");
        Assert.Same(overrideModule, found);
        // The other recon verbs still load alongside the override.
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();
        Assert.Contains(ReconCapabilities.HostEnum, verbs);
    }

    // A module whose result is fixed at construction, so a test can stand in for
    // an out-of-tree override without writing real tradecraft. Mirrors the helper
    // in CoreCapabilitiesTests.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }
        private readonly string _output;

        public FixedModule(string verb, string output)
        {
            Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Recon, "1.0");
            _output = output;
        }

        public Task<CapabilityResult> ExecuteAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CapabilityResult.Succeeded(_output));
    }
}
