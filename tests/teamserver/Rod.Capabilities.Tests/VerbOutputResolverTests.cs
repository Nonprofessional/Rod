using Rod.Capabilities.Core;
using Rod.Capabilities.Modules;
using Rod.Capabilities.Recon;
using Rod.Capabilities.Registry;

namespace Rod.Capabilities.Tests;

/// <summary>
/// The output-taxonomy resolvers (architecture.md Sec 15.2's second
/// contract): the registry-backed implementation the composition root swaps
/// in answers from each verb's declaration, and the static fallback keeps
/// the built-in projection set parsing in hosts that never opt into the
/// capabilities layer.
/// </summary>
public class VerbOutputResolverTests
{
    private sealed class Stub : ICapabilityModule
    {
        public Stub(CapabilityDescriptor descriptor) => Descriptor = descriptor;

        public CapabilityDescriptor Descriptor { get; }
    }

    [Fact]
    public async Task RegistryBackedResolver_AnswersFromTheDeclaration()
    {
        // A registered verb answers with its declared kind, an unregistered
        // verb answers null (plain text, the honest degradation), and the
        // built-ins answer without any registration beyond the standard
        // load.
        var registry = await RodCapabilitiesHost.BuildDefaultRegistryAsync();
        var resolver = new CapabilityRegistryVerbOutputResolver(registry);

        Assert.Equal(Rod.CoreState.Implants.TaskOutputKind.Enumeration, resolver.OutputKind(ReconCapabilities.PortScan));
        Assert.Equal(Rod.CoreState.Implants.TaskOutputKind.File, resolver.OutputKind(CoreCapabilities.FilePull));
        Assert.Null(resolver.OutputKind("never.registered"));

        await registry.RegisterAsync(new Stub(
            CapabilityDescriptor.Of("sweep.discover", CapabilityCategory.Recon, "0.1",
                output: new VerbOutput(VerbOutputKind.Enumeration))));
        Assert.Equal(Rod.CoreState.Implants.TaskOutputKind.Enumeration, resolver.OutputKind("sweep.discover"));
    }

    [Fact]
    public void DefaultResolver_AnswersTheBuiltInProjectionSet()
    {
        // The static fallback hosts without the capabilities layer run on:
        // the verbs whose grammar the read-side projections parse, so a
        // plain transport host keeps parsing recon output exactly as before.
        var resolver = new Rod.CoreState.Implants.DefaultVerbOutputResolver();

        Assert.Equal(Rod.CoreState.Implants.TaskOutputKind.Enumeration, resolver.OutputKind(ReconCapabilities.PortScan));
        Assert.Equal(Rod.CoreState.Implants.TaskOutputKind.Enumeration, resolver.OutputKind(ReconCapabilities.HostEnum));
        Assert.Equal(Rod.CoreState.Implants.TaskOutputKind.Credential, resolver.OutputKind(Rod.Capabilities.Collect.CollectCapabilities.Cred));
        Assert.Equal(Rod.CoreState.Implants.TaskOutputKind.File, resolver.OutputKind(CoreCapabilities.FilePull));
        Assert.Equal(Rod.CoreState.Implants.TaskOutputKind.Screenshot, resolver.OutputKind(Rod.Capabilities.Collect.CollectCapabilities.Screenshot));
        Assert.Null(resolver.OutputKind("never.registered"));
    }
}
