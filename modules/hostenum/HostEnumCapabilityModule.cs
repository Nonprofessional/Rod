using Rod.Capabilities;
using Rod.Capabilities.Modules;

namespace Rod.Modules.Hostenum;

/// <summary>
/// The server-side half of the in-tree hostenum reference module
/// (architecture.md Sec 15.1): the descriptor registration for
/// <c>recon.hostenum</c>, whose execution half is the implant-side plugin at
/// <c>src/implant/rust/modules/hostenum</c>. The tree ships both halves as the
/// worked example of the module seam -- an operator's own module copies either
/// half, or writes a fresh pair against the same contracts.
/// </summary>
/// <remarks>
/// This module is deliberately indistinguishable from an external one: it is a
/// separate assembly the teamserver never compile-references, deployed beside
/// the binary and listed under <c>Capabilities:Modules</c> in the shipped
/// configuration, so it reaches the registry through the same
/// loader path an out-of-tree module takes and replaces the built-in
/// placeholder for its verb (last registration wins). Removing the config
/// entry drops back to the placeholder -- the same authority rule every
/// module rides, in-tree or not.
/// </remarks>
public sealed class HostEnumCapabilityModule : ICapabilityModule
{
    /// <summary>
    /// The verb string both halves declare independently: the implant plugin
    /// registers <c>recon.hostenum</c> against the plugin SDK, and this
    /// descriptor registers the same verb with the server-side registry. The
    /// string -- not a shared type -- is the contract between them
    /// (architecture.md Sec 10).
    /// </summary>
    public CapabilityDescriptor Descriptor { get; } = CapabilityDescriptor.Of(
        "recon.hostenum",
        CapabilityCategory.Recon,
        "1.0");
}
