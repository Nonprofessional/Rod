using Rod.CoreState.Implants;
using Rod.Tradecraft;
using Rod.Tradecraft.Browser;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// Contract-layer acceptance: the browser hook verbs (architecture.md Sec
/// 5.2, Sec 10.1) load through the tradecraft registry alongside the core
/// set, are listed in the Browser category, register as placeholders (the
/// handlers live in the served hook script, not in any compiled implant),
/// carry their OPSEC attributes, and line up with the class table the task
/// gate reads.
/// </summary>
public class BrowserCapabilitiesTests
{
    [Fact]
    public async Task DefaultRegistry_ListsEveryBrowserVerb()
    {
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var descriptors = await registry.ListAsync();

        var byVerb = descriptors.ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);
        foreach (var verb in BrowserCapabilities.Verbs)
        {
            Assert.True(byVerb.ContainsKey(verb), $"browser verb '{verb}' is not registered");
            Assert.Equal(CapabilityCategory.Browser, byVerb[verb].Category);
        }
    }

    [Fact]
    public async Task DefaultRegistry_RegistersTheBrowserVerbs_AsPlaceholders()
    {
        // The server gates and forwards only (architecture.md Sec 10.2/10.3):
        // execution lives in the hook script the teamserver serves.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();

        var found = await registry.FindAsync(BrowserCapabilities.Fingerprint);
        Assert.IsType<PlaceholderCapabilityModule>(found);
    }

    [Fact]
    public async Task DefaultRegistry_CarriesTheOPSECAttributes()
    {
        // The read verbs carry what they read, redirect carries its effect,
        // and prompt carries reads-input so the sensitive-verb policy holds
        // it out of automation (Sec 10.4) while hand tasking stays open.
        var registry = await RodTradecraftHost.BuildDefaultRegistryAsync();
        var descriptors = (await registry.ListAsync())
            .ToDictionary(d => d.Verb, StringComparer.OrdinalIgnoreCase);

        Assert.True(descriptors[BrowserCapabilities.Cookies].Attributes.ContainsKey("reads-cookies"));
        Assert.True(descriptors[BrowserCapabilities.Dom].Attributes.ContainsKey("reads-page"));
        Assert.True(descriptors[BrowserCapabilities.Screenshot].Attributes.ContainsKey("captures-screen"));
        Assert.True(descriptors[BrowserCapabilities.Redirect].Attributes.ContainsKey("navigates-away"));
        Assert.True(descriptors[BrowserCapabilities.Prompt].Attributes.ContainsKey("reads-input"));
    }

    [Fact]
    public void BrowserVerbs_AreExactlyTheBrowserClassSet()
    {
        // The registry catalog and the class table must agree (architecture.md
        // Sec 5.2): the browser category is the Browser class's whole set,
        // nothing more, nothing less.
        Assert.Equal(
            ImplantClassCapabilities.For(ImplantClass.Browser).OrderBy(v => v, StringComparer.OrdinalIgnoreCase),
            BrowserCapabilities.Verbs.OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadCapabilities_LeavesCallerBrowserOverrideInPlace()
    {
        // An out-of-tree browser module registered before the built-in load
        // stays the authority for its verb: the loader deduplicates against
        // what the registry already holds, the same rule that protects every
        // other category's overrides.
        var registry = new InMemoryCapabilityRegistry();
        var overrideModule = new FixedModule(BrowserCapabilities.Fingerprint);
        await registry.RegisterAsync(overrideModule);

        await RodTradecraftHost.LoadCapabilitiesAsync(registry);

        var found = await registry.FindAsync(BrowserCapabilities.Fingerprint);
        Assert.Same(overrideModule, found);
    }

    // A module whose descriptor is fixed at construction, so a test can stand in
    // for an out-of-tree override without writing real tradecraft.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }

        public FixedModule(string verb)
            => Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Browser, "1.0");
    }
}
