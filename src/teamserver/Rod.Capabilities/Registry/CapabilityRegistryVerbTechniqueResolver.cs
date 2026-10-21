using Rod.CoreState.Implants;

namespace Rod.Capabilities.Registry;

/// <summary>
/// The registry-backed <see cref="IVerbTechniqueResolver"/> (architecture.md
/// Sec 15.3, ADR-007): a verb's techniques are its registered descriptor's
/// declaration when it carries one -- replacing the kernel mapping's whole
/// entry for that verb, the same authority a registration holds everywhere
/// else -- and otherwise the kernel data file's entry, which is what every
/// framework verb resolves through. The composition root swaps this in over
/// core state's static fallback, so the coverage matrix, the report's
/// Navigator layer, and the catalog all read one answer.
/// </summary>
public sealed class CapabilityRegistryVerbTechniqueResolver : IVerbTechniqueResolver
{
    private readonly ICapabilityRegistry _registry;

    public CapabilityRegistryVerbTechniqueResolver(ICapabilityRegistry registry)
    {
        _registry = registry;
    }

    public IReadOnlyList<string>? Techniques(string verb)
        => _registry.FindAsync(verb).GetAwaiter().GetResult() is { Descriptor.Techniques: { } declared }
            ? declared
            : AttackTechniqueCatalog.Verbs.TryGetValue(verb, out var mapped)
                ? mapped
                : null;
}
