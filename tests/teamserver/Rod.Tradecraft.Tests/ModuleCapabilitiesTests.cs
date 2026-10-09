using Rod.CoreState.Implants;
using Rod.Tradecraft;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Module;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// Contract-layer acceptance: the module family (architecture.md Sec 5.4 --
/// the implant-side plugin seam's own verbs) loads through the tradecraft
/// registry beside the standard categories, is listed in the Module
/// category, registers as placeholders (execution lives on the implant's
/// plugin table), carries the unattended-posture attribute on the loading
/// verb, and is gated to the Implant class.
/// </summary>
public class ModuleCapabilitiesTests
{
    [Fact]
    public async Task DefaultRegistry_ListsEveryModuleVerb()
    {
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var byVerb = (await registry.ListAsync())
            .ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);

        foreach (var verb in ModuleCapabilities.Verbs)
        {
            Assert.True(byVerb.ContainsKey(verb), $"module verb '{verb}' is not registered");
            Assert.Equal(CapabilityCategory.Module, byVerb[verb].Category);
        }
    }

    [Fact]
    public async Task DefaultRegistry_FlagsTheLoadingVerb_AsExecutingCode()
    {
        // Loading a module is running new code on the target: the attribute
        // badges the picker and keeps the automation engine from firing it
        // unattended (architecture.md Sec 7, Sec 10.4).
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var byVerb = (await registry.ListAsync())
            .ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);

        Assert.True(byVerb[ModuleCapabilities.ModuleLoad].Attributes.TryGetValue("executes-code", out var value));
        Assert.Equal("true", value);
        // The read verbs carry no such flag -- listing is not acting.
        Assert.DoesNotContain(byVerb[ModuleCapabilities.ModuleList].Attributes, a => a.Key == "executes-code");
        Assert.DoesNotContain(byVerb[ModuleCapabilities.ModuleUnload].Attributes, a => a.Key == "executes-code");
    }

    [Fact]
    public async Task DefaultRegistry_RegistersTheModuleVerbs_AsPlaceholders()
    {
        // The server gates and forwards only (architecture.md Sec 10.2): the
        // module verbs register as placeholders, and execution lives on the
        // implant's plugin table.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var found = await registry.FindAsync(ModuleCapabilities.ModuleLoad);
        Assert.IsType<PlaceholderCapabilityModule>(found);
    }

    [Fact]
    public void TheModuleFamily_IsImplantClassGated()
    {
        // Module support is the long-haul class's (architecture.md Sec 5.2):
        // no reduced class but the Implant's carries the family.
        foreach (var verb in ModuleCapabilities.Verbs)
        {
            Assert.True(ImplantClassCapabilities.Allows(ImplantClass.Implant, verb));
            Assert.False(ImplantClassCapabilities.Allows(ImplantClass.WebShell, verb));
            Assert.False(ImplantClassCapabilities.Allows(ImplantClass.Ephemeral, verb));
            Assert.False(ImplantClassCapabilities.Allows(ImplantClass.Pivot, verb));
        }
    }

    [Fact]
    public async Task LoadCapabilities_LeavesCallerModuleOverrideInPlace()
    {
        // An out-of-tree module registered before the built-in load stays the
        // authority for its verb: the loader deduplicates against what the
        // registry already holds, the same rule every category follows.
        var registry = new InMemoryCapabilityRegistry();
        var overrideModule = new FixedModule("module.load");
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync("module.load");
        Assert.Same(overrideModule, found);
    }

    // A module whose descriptor is fixed at construction, so a test can stand in
    // for an out-of-tree override without writing real tradecraft.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }

        public FixedModule(string verb)
            => Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Module, "1.0");
    }
}
