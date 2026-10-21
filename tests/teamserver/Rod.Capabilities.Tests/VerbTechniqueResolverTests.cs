using Rod.Capabilities.Core;
using Rod.Capabilities.Modules;
using Rod.Capabilities.Recon;
using Rod.Capabilities.Registry;
using Rod.CoreState.Implants;

namespace Rod.Capabilities.Tests;

/// <summary>
/// The ATT&CK technique resolvers (architecture.md Sec 15.3, ADR-007): the
/// registry-backed implementation the composition root swaps in lets a
/// module's declaration replace the kernel data file's entry for its verb,
/// and the static fallback serves the kernel mapping in hosts that never
/// load a module -- so every consumer face (matrix, catalog, Navigator
/// layer) reads one answer whatever the host.
/// </summary>
public class VerbTechniqueResolverTests
{
    private sealed class Stub : ICapabilityModule
    {
        public Stub(CapabilityDescriptor descriptor) => Descriptor = descriptor;

        public CapabilityDescriptor Descriptor { get; }
    }

    [Fact]
    public async Task RegistryBackedResolver_FrameworkVerbs_AnswerFromTheKernelFile()
    {
        var registry = await RodCapabilitiesHost.BuildDefaultRegistryAsync();
        var resolver = new CapabilityRegistryVerbTechniqueResolver(registry);

        // The placeholders declare no techniques, so the kernel file's entries
        // stand -- the default posture of a host with no modules installed.
        Assert.Equal(new[] { "T1005", "T1041" }, resolver.Techniques(CoreCapabilities.FilePull));
        Assert.Equal(new[] { "T1082" }, resolver.Techniques(ReconCapabilities.HostEnum));
        Assert.Null(resolver.Techniques(CoreCapabilities.ProcKill));
        Assert.Null(resolver.Techniques("never.registered"));
    }

    [Fact]
    public async Task RegistryBackedResolver_AModuleDeclaration_ReplacesTheKernelEntry()
    {
        var registry = await RodCapabilitiesHost.BuildDefaultRegistryAsync();
        var resolver = new CapabilityRegistryVerbTechniqueResolver(registry);

        // A module taking over a framework verb with its own declaration is
        // the whole entry -- replace, not union -- mirroring the authority a
        // registration carries everywhere else.
        await registry.RegisterAsync(new Stub(
            CapabilityDescriptor.Of(ReconCapabilities.HostEnum, CapabilityCategory.Recon, "9.9",
                techniques: ["T1016", "T1018"])));
        Assert.Equal(new[] { "T1016", "T1018" }, resolver.Techniques(ReconCapabilities.HostEnum));
    }

    [Fact]
    public async Task RegistryBackedResolver_AModuleWithoutDeclaration_LeavesTheKernelEntry()
    {
        var registry = await RodCapabilitiesHost.BuildDefaultRegistryAsync();
        var resolver = new CapabilityRegistryVerbTechniqueResolver(registry);

        // A module that implements a verb without mapping it does not erase
        // the kernel's opinion: null is "no declaration", not "no techniques".
        await registry.RegisterAsync(new Stub(
            CapabilityDescriptor.Of(ReconCapabilities.HostEnum, CapabilityCategory.Recon, "9.9")));
        Assert.Equal(new[] { "T1082" }, resolver.Techniques(ReconCapabilities.HostEnum));
    }

    [Fact]
    public async Task RegistryBackedResolver_ANewModuleVerb_AnswersFromItsDeclaration()
    {
        var registry = await RodCapabilitiesHost.BuildDefaultRegistryAsync();
        var resolver = new CapabilityRegistryVerbTechniqueResolver(registry);

        // A community verb the kernel file never knew: the declaration is the
        // only mapping there is, which is how coverage extends module by
        // module without a kernel edit.
        await registry.RegisterAsync(new Stub(
            CapabilityDescriptor.Of("sweep.discover", CapabilityCategory.Recon, "0.1",
                techniques: ["T1046"])));
        Assert.Equal(new[] { "T1046" }, resolver.Techniques("sweep.discover"));
    }

    [Fact]
    public async Task Registration_RejectsAMalformedDeclaration()
    {
        var registry = await RodCapabilitiesHost.BuildDefaultRegistryAsync();

        // The ids render verbatim on the matrix and inside exported Navigator
        // layers, so a malformed declaration is refused as loudly as a broken
        // parameter schema.
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RegisterAsync(new Stub(
            CapabilityDescriptor.Of("sweep.bad", CapabilityCategory.Recon, "0.1",
                techniques: ["T105"]))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RegisterAsync(new Stub(
            CapabilityDescriptor.Of("sweep.bad", CapabilityCategory.Recon, "0.1",
                techniques: []))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RegisterAsync(new Stub(
            CapabilityDescriptor.Of("sweep.bad", CapabilityCategory.Recon, "0.1",
                techniques: ["T1046", "T1046"]))));
    }

    [Fact]
    public void DefaultResolver_AnswersTheKernelFile()
    {
        IVerbTechniqueResolver resolver = new DefaultVerbTechniqueResolver();
        Assert.Equal(new[] { "T1082" }, resolver.Techniques(ReconCapabilities.HostEnum));
        Assert.Null(resolver.Techniques("never.registered"));
    }
}
