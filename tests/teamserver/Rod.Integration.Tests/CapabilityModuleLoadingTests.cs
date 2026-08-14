using Microsoft.Extensions.DependencyInjection;
using Rod.Tradecraft;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Evasion;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Registry;

namespace Rod.Integration.Tests;

/// <summary>
/// The out-of-tree module loading acceptance (architecture.md Sec 10.2): a
/// module listed under <c>Tradecraft:Modules</c> loads at startup, replaces the
/// placeholder for its verb, and is reached from the live task path -- all
/// without any composition-root edit. The config names the test assembly's own
/// module type; a production module is the same shape in an assembly placed next
/// to the teamserver binary.
/// </summary>
public class CapabilityModuleLoadingTests
{
    // A module built against the contract, listed by config. The exact shape an
    // operator-supplied out-of-tree assembly provides: a descriptor and nothing
    // else -- the server gates and forwards, it never executes (Sec 10.2/10.3).
    public sealed class ConfigListedEvasionModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; } =
            CapabilityDescriptor.Of(EvasionCapabilities.Avoid, CapabilityCategory.Evasion, "1.0");
    }

    private const string ModuleEntry =
        "Rod.Integration.Tests.CapabilityModuleLoadingTests+ConfigListedEvasionModule, Rod.Integration.Tests";

    [Fact]
    public async Task ConfigListedModule_LoadsAtStartupAndReplacesThePlaceholder()
    {
        // The composition root loads the module from the config list without
        // editing any core code: the registry's authority for evasion.avoid is
        // the config-listed module, not the built-in placeholder.
        var (client, host, _) = AuthenticatedHost.Create(
            extendConfig: settings => settings["Tradecraft:Modules:0"] = ModuleEntry,
            configureServicesWithConfig: (services, config) => services.AddRodTradecraft(config));
        using (client)
        using (host)
        {
            var registry = host.Services.GetRequiredService<ICapabilityRegistry>();

            var found = await registry.FindAsync(EvasionCapabilities.Avoid);

            Assert.NotNull(found);
            Assert.IsType<ConfigListedEvasionModule>(found);
            Assert.Equal(EvasionCapabilities.Avoid, found!.Descriptor.Verb);
        }
    }

    [Fact]
    public void ConfigListedModule_MissingAssembly_FailsStartup()
    {
        // A module entry that cannot resolve must fail startup loudly: silently
        // keeping the placeholder would leave the verb "registered but not what
        // the operator deployed" -- the failure mode the loader exists to avoid.
        Assert.Throws<InvalidOperationException>(() =>
            AuthenticatedHost.Create(
                extendConfig: settings => settings["Tradecraft:Modules:0"] = "No.Such.Type, No.Such.Assembly",
                configureServicesWithConfig: (services, configuration) =>
                    services.AddRodTradecraft(configuration)));
    }

    [Fact]
    public void ConfigListedModule_NonModuleType_FailsStartup()
    {
        // A listed type that does not implement the contract is refused: the
        // loader registers modules only, so a misconfigured entry cannot smuggle
        // an arbitrary object into the registry.
        Assert.Throws<InvalidOperationException>(() =>
            AuthenticatedHost.Create(
                extendConfig: settings =>
                    settings["Tradecraft:Modules:0"] = "Rod.Integration.Tests.CapabilityModuleLoadingTests, Rod.Integration.Tests",
                configureServicesWithConfig: (services, configuration) =>
                    services.AddRodTradecraft(configuration)));
    }
}
