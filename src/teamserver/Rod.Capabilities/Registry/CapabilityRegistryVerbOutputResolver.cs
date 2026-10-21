using Rod.CoreState.Implants;

namespace Rod.Capabilities.Registry;

/// <summary>
/// The registry-backed <see cref="IVerbOutputResolver"/> (architecture.md Sec
/// 15.2, the contract trilogy's second contract): a verb's output kind is
/// whatever its registered <see cref="CapabilityDescriptor"/> declares. The
/// composition root swaps this in over core state's static fallback, so the
/// read-side projections -- topology, loot classification, the credentials
/// view -- parse any registered verb's structured output, the framework's or
/// a module's, with no verb list to drift.
/// </summary>
public sealed class CapabilityRegistryVerbOutputResolver : IVerbOutputResolver
{
    private readonly ICapabilityRegistry _registry;

    public CapabilityRegistryVerbOutputResolver(ICapabilityRegistry registry)
    {
        _registry = registry;
    }

    public TaskOutputKind? OutputKind(string verb)
        => _registry.FindAsync(verb).GetAwaiter().GetResult() is { } module
            ? Map(module.Descriptor.Output?.Kind)
            : null;

    internal static TaskOutputKind? Map(VerbOutputKind? kind)
        => kind switch
        {
            null => null,
            VerbOutputKind.Text => TaskOutputKind.Text,
            VerbOutputKind.Enumeration => TaskOutputKind.Enumeration,
            VerbOutputKind.ProcessList => TaskOutputKind.ProcessList,
            VerbOutputKind.FileList => TaskOutputKind.FileList,
            VerbOutputKind.Credential => TaskOutputKind.Credential,
            VerbOutputKind.File => TaskOutputKind.File,
            VerbOutputKind.Screenshot => TaskOutputKind.Screenshot,
            _ => null,
        };
}
