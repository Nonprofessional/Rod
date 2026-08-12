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
/// Roadmap M5.4 acceptance at the contract layer: the collection verbs
/// (architecture.md Sec 10.1) load through the tradecraft registry alongside the
/// core, recon, lateral, persist, and exfil sets, are listed in the Collect
/// category, dispatch as registered-but-not-implemented (their concrete behavior
/// is out-of-tree, like the non-shell core verbs and the recon, lateral, and
/// persist verbs), carry their OPSEC attributes, and respect the same
/// out-of-tree-override rule.
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
        // (architecture.md Sec 7): collect.file reads the filesystem,
        // collect.cred reads a credential, and collect.keylog installs a resident
        // input-capture hook so it both reads input and persists on the target.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var descriptors = (await registry.ListAsync())
            .ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);

        Assert.True(descriptors[CollectCapabilities.File].Attributes.TryGetValue("reads-filesystem", out var fileFs));
        Assert.Equal("true", fileFs);
        Assert.True(descriptors[CollectCapabilities.Cred].Attributes.TryGetValue("reads-credential", out var credCred));
        Assert.Equal("true", credCred);
        Assert.True(descriptors[CollectCapabilities.Keylog].Attributes.TryGetValue("reads-input", out var keylogInput));
        Assert.Equal("true", keylogInput);
        Assert.True(descriptors[CollectCapabilities.Keylog].Attributes.TryGetValue("persists", out var keylogPersists));
        Assert.Equal("true", keylogPersists);
        // The filesystem and credential reads do not install a resident hook.
        Assert.False(descriptors[CollectCapabilities.File].Attributes.ContainsKey("persists"));
        Assert.False(descriptors[CollectCapabilities.Cred].Attributes.ContainsKey("persists"));
    }

    [Fact]
    public async Task DefaultRegistry_DispatchesACollectVerb_AsRegisteredButNotImplemented()
    {
        // Concrete collection behavior is out-of-tree (architecture.md Sec 13,
        // AGENTS.md Sec 7), so dispatching a collect verb against the default
        // registry reports a failure -- the verb is known, just unimplemented
        // in-process -- the same outcome the non-shell core verbs and the recon,
        // lateral, and persist verbs produce.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var dispatcher = new CapabilityDispatcher(registry);

        var result = await dispatcher.DispatchAsync(
            new CapabilityInvocation(CollectCapabilities.File, ""));

        Assert.Equal(CapabilityStatus.Failed, result.Status);
        Assert.Contains(CollectCapabilities.File, result.Error ?? string.Empty);
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
        var overrideModule = new FixedModule("collect.file", "real collect module");
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync("collect.file");
        Assert.Same(overrideModule, found);
        // The other collect verbs still load alongside the override.
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();
        Assert.Contains(CollectCapabilities.Cred, verbs);
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
            Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Collect, "1.0");
            _output = output;
        }

        public Task<CapabilityResult> ExecuteAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CapabilityResult.Succeeded(_output));
    }
}
