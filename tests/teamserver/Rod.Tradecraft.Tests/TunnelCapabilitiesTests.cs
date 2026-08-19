using Rod.Tradecraft;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Registry;
using Rod.Tradecraft.Tunnel;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// Contract-layer acceptance: the tunnel verbs
/// (architecture.md Sec 5.2, Sec 14) load through the tradecraft registry
/// alongside the core set, are listed in the Tunnel category, register as
/// placeholders (their concrete behavior runs on the implant as a live
/// channel, architecture.md Sec 10.3), and respect the same
/// out-of-tree-override rule.
/// </summary>
public class TunnelCapabilitiesTests
{
    [Fact]
    public async Task DefaultRegistry_ListsEveryTunnelVerb()
    {
        // The tunnel verbs load through the registry: each appears in the
        // tunnel category after the default registry is built.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var descriptors = await registry.ListAsync();

        var byVerb = descriptors.ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);
        foreach (var verb in TunnelCapabilities.Verbs)
        {
            Assert.True(byVerb.ContainsKey(verb), $"tunnel verb '{verb}' is not registered");
            Assert.Equal(CapabilityCategory.Tunnel, byVerb[verb].Category);
        }
    }

    [Fact]
    public async Task DefaultRegistry_FlagsNetworkTouchingVerbs_InAttributes()
    {
        // Every tunnel verb opens a network connection from the target, so it
        // carries a touches-network OPSEC attribute for operators and
        // tradecraft filters to surface (architecture.md Sec 7).
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var descriptors = (await registry.ListAsync())
            .ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);

        Assert.True(descriptors[TunnelCapabilities.Forward].Attributes.TryGetValue("touches-network", out var value));
        Assert.Equal("true", value);
    }

    [Fact]
    public async Task DefaultRegistry_RegistersTheTunnelVerbs_AsPlaceholders()
    {
        // The server gates and forwards only (architecture.md Sec 10.2/10.3):
        // the tunnel verbs register as placeholders, and execution lives on the
        // implant's channel handler.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var found = await registry.FindAsync(TunnelCapabilities.Forward);
        Assert.IsType<PlaceholderCapabilityModule>(found);
    }

    [Fact]
    public async Task DefaultRegistry_ListsTheTunnelSetAlongsideCore()
    {
        // The default registry is the union of every built-in set: each tunnel
        // verb is present alongside the core verbs, so the operator-visible
        // capability surface is the full built-in set.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var verbs = (await registry.ListAsync()).Select(d => d.Verb).ToArray();

        foreach (var verb in CoreCapabilities.Verbs)
            Assert.Contains(verb, verbs);
        foreach (var verb in TunnelCapabilities.Verbs)
            Assert.Contains(verb, verbs);
    }

    [Fact]
    public async Task LoadCapabilities_LeavesCallerTunnelOverrideInPlace()
    {
        // An out-of-tree tunnel module registered before the built-in load must
        // stay the authority for its verb: the loader deduplicates against what
        // the registry already holds, the same rule that protects core and
        // recon overrides.
        var registry = new InMemoryCapabilityRegistry();
        var overrideModule = new FixedModule("tunnel.forward");
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync("tunnel.forward");
        Assert.Same(overrideModule, found);
    }

    // A module whose descriptor is fixed at construction, so a test can stand in
    // for an out-of-tree override without writing real tradecraft.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }

        public FixedModule(string verb)
            => Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Tunnel, "1.0");
    }
}
