using Rod.Capabilities.Core;
using Rod.Capabilities.Modules;
using Rod.Capabilities.Recon;
using Rod.Capabilities.Registry;

namespace Rod.Capabilities.Tests;

/// <summary>
/// The contract-trilogy declarations (architecture.md Sec 15.2, ADR-003):
/// the parameter schema's own invariants (registration refuses a form the
/// UI cannot render), the built-in sets' declarations (every verb the
/// reference grammars cover carries its schema and output kind), and the
/// registry-backed output-taxonomy resolver every read-side projection
/// consults.
/// </summary>
public class VerbContractTests
{
    private sealed class Stub : ICapabilityModule
    {
        public Stub(CapabilityDescriptor descriptor) => Descriptor = descriptor;

        public CapabilityDescriptor Descriptor { get; }
    }

    private static CapabilityDescriptor Describe(params VerbParameter[] parameters)
        => CapabilityDescriptor.Of("demo.run", CapabilityCategory.Core, "1.0", parameters: parameters);

    [Fact]
    public async Task Register_RefusesASchemaTheUICannotRender()
    {
        // A trailing parameter anywhere but last, a duplicate field name, and
        // an enum without choices each refuse registration -- loudly, at the
        // moment the module arrives, the same discipline every other
        // misconfiguration already holds.
        var registry = new InMemoryCapabilityRegistry();

        var trailingNotLast = Describe(
            new VerbParameter("cmd", "Command", VerbParameterKind.Text, Trailing: true),
            new VerbParameter("extra", "Extra", VerbParameterKind.String));
        var duplicate = Describe(
            new VerbParameter("host", "Host", VerbParameterKind.String),
            new VerbParameter("host", "Host", VerbParameterKind.String));
        var choiceless = Describe(
            new VerbParameter("mode", "Mode", VerbParameterKind.Enum));

        foreach (var broken in new[] { trailingNotLast, duplicate, choiceless })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => registry.RegisterAsync(new Stub(broken)));
        }
    }

    [Fact]
    public async Task Register_AcceptsAWellFormedSchema()
    {
        var registry = new InMemoryCapabilityRegistry();
        var descriptor = Describe(
            new VerbParameter("host", "Host", VerbParameterKind.String, Required: true),
            new VerbParameter("mode", "Mode", VerbParameterKind.Enum, Choices: ["fast", "quiet"]),
            new VerbParameter("payload", "Payload", VerbParameterKind.Text, Required: true, Trailing: true));

        await registry.RegisterAsync(new Stub(descriptor));

        Assert.NotNull(await registry.FindAsync("demo.run"));
    }

    [Fact]
    public void BuiltInSets_DeclareTheirSchemasAndOutputKinds()
    {
        // The verbs whose grammars the UI and the read-side projections
        // cover all carry their declarations: the form verbs carry schemas,
        // the structural verbs carry output kinds. A verb without either
        // still registers (the honest raw-argument fallback), so this pins
        // the covered set rather than demanding blanket coverage.
        var descriptors = new Dictionary<string, CapabilityDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in CoreCapabilities.All
                     .Concat(ReconCapabilities.All)
                     .Concat(Rod.Capabilities.Collect.CollectCapabilities.All)
                     .Concat(Rod.Capabilities.Persist.PersistCapabilities.All)
                     .Concat(Rod.Capabilities.Module.ModuleCapabilities.All)
                     .Concat(Rod.Capabilities.Exfil.ExfilCapabilities.All))
        {
            descriptors[descriptor.Verb] = descriptor;
        }

        var shell = descriptors[CoreCapabilities.ShellExec];
        Assert.NotNull(shell.Parameters);
        Assert.Single(shell.Parameters!);
        Assert.True(shell.Parameters[0].Trailing);

        var push = descriptors[CoreCapabilities.FilePush];
        Assert.Equal(2, push.Parameters!.Count);
        Assert.Contains(push.Parameters, p => p.Kind == VerbParameterKind.File && p.Inline);

        var load = descriptors[Rod.Capabilities.Module.ModuleCapabilities.ModuleLoad];
        Assert.Contains(load.Parameters!, p => p.Kind == VerbParameterKind.File && !p.Inline);

        Assert.Equal(VerbOutputKind.FileList, descriptors[CoreCapabilities.FsList].Output!.Kind);
        Assert.Equal(VerbOutputKind.Enumeration, descriptors[ReconCapabilities.PortScan].Output!.Kind);
        Assert.Equal(VerbOutputKind.Enumeration, descriptors[ReconCapabilities.HostEnum].Output!.Kind);
        Assert.Equal(VerbOutputKind.ProcessList, descriptors[ReconCapabilities.Ps].Output!.Kind);
        Assert.Equal(VerbOutputKind.Credential, descriptors[Rod.Capabilities.Collect.CollectCapabilities.Cred].Output!.Kind);
        Assert.Equal(VerbOutputKind.File, descriptors[CoreCapabilities.FilePull].Output!.Kind);
        Assert.Equal(VerbOutputKind.Screenshot, descriptors[Rod.Capabilities.Collect.CollectCapabilities.Screenshot].Output!.Kind);
    }
}
