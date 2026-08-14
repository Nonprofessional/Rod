using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Modules;

namespace Rod.Tradecraft.Core;

/// <summary>
/// A registration-only module: it declares a <see cref="CapabilityDescriptor"/>
/// so the registry lists the verb and the task-issuance gate admits it, but no
/// behavior runs in-process -- verb execution lives on the implant
/// (architecture.md Sec 5.3, Sec 10.2/10.3). Every built-in verb, core included,
/// registers through this shape so the capability catalog lists the full
/// framework set without committing any in-process tradecraft.
/// </summary>
/// <remarks>
/// One instance carries one verb. An operator-supplied out-of-tree module
/// registered for the same verb replaces the placeholder (last registration
/// wins), which is the whole out-of-tree path: a registration, never a schema
/// change or a composition-root edit (architecture.md Sec 10.2, AGENTS.md Sec 7).
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
}
