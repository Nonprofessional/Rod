using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Modules;

namespace Rod.Tradecraft.Core;

/// <summary>
/// A registration-only module: it declares a <see cref="CapabilityDescriptor"/>
/// so the registry lists the verb, but dispatch returns a "not implemented"
/// failure. Used for the core verbs whose concrete behavior runs on the implant
/// (architecture.md Sec 10.3) and is not part of this repository -- they must
/// appear in the registry (the core verbs load through it) without committing
/// any in-process tradecraft.
/// </summary>
/// <remarks>
/// One instance carries one verb. The composition root creates one per
/// not-yet-implemented core verb; replacing one with a real module is a later
/// <see cref="ICapabilityModule"/> implementation registered for the same verb.
/// </remarks>
public sealed class PlaceholderCapabilityModule : ICapabilityModule
{
    /// <summary>The descriptor this placeholder registers under.</summary>
    public CapabilityDescriptor Descriptor { get; }

    /// <summary>Builds a placeholder for <paramref name="descriptor"/>'s verb.</summary>
    public PlaceholderCapabilityModule(CapabilityDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    /// <summary>
    /// Always returns a failure explaining the verb is registered but has no
    /// in-process implementation. Concrete behavior is out-of-tree
    /// (architecture.md Sec 13, AGENTS.md Sec 7).
    /// </summary>
    public Task<CapabilityResult> ExecuteAsync(
        CapabilityInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CapabilityResult.Failed(
            $"'{Descriptor.Verb}' is registered but has no in-process implementation."));
    }
}
