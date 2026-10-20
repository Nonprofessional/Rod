using Microsoft.Extensions.DependencyInjection;
using Rod.Capabilities;
using Rod.Capabilities.Registry;
using Rod.Modules.Hostenum;

namespace Rod.Integration.Tests;

/// <summary>
/// The in-tree module acceptance (architecture.md Sec 15.1): the reference
/// modules under <c>modules/</c> load through exactly the path an external
/// module takes -- a <c>Capabilities:Modules</c> config entry naming a type in
/// an assembly the loader resolves by name, instantiated at startup and
/// replacing the built-in placeholder for the verb. The test host references
/// the module project only so its assembly lands in the test bin directory,
/// the stand-in for the deployment step (the teamserver build copying the
/// module dll beside the binary) -- no test or kernel code touches the
/// module's types directly.
/// </summary>
public class InTreeModuleLoadingTests
{
    [Fact]
    public async Task InTreeModule_LoadsThroughTheConfigListedPath_AndReplacesThePlaceholder()
    {
        // The entry is the exact string the shipped appsettings.json carries:
        // the same loader, the same config list, the same
        // last-registration-wins replacement an operator's external module
        // rides. Nothing about the module being in-tree changes the path.
        var (client, host, _) = AuthenticatedHost.Create(
            extendConfig: settings => settings["Capabilities:Modules:0"] =
                "Rod.Modules.Hostenum.HostEnumCapabilityModule, Rod.Modules.Hostenum",
            configureServicesWithConfig: (services, config) => services.AddRodCapabilities(config));
        using (client)
        using (host)
        {
            var registry = host.Services.GetRequiredService<ICapabilityRegistry>();

            var found = await registry.FindAsync("recon.hostenum");

            // The in-tree module is the authority for the verb, not the
            // built-in placeholder: the config-listed registration won.
            Assert.NotNull(found);
            Assert.IsType<HostEnumCapabilityModule>(found);
            Assert.Equal("recon.hostenum", found!.Descriptor.Verb);
        }
    }
}
