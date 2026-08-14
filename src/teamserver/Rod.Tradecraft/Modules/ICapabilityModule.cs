using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Modules;

/// <summary>
/// One pluggable post-exploitation capability module (architecture.md Sec 4.1
/// layer 6, Sec 10/13). A module declares the single capability it provides via
/// <see cref="Descriptor"/>; the registry indexes modules by that verb so the
/// task-issuance gate and the capability catalog can read them.
/// </summary>
/// <remarks>
/// This is a registration contract, not an execution contract: the teamserver
/// only gates and forwards on the live task path and never invokes a capability
/// module server-side -- verb execution and dispatch live on the implant
/// (architecture.md Sec 5.3, Sec 10.2/10.3), where the target's filesystem,
/// network, and credentials actually exist. A module therefore carries exactly
/// the declaration the server needs: the descriptor it registers under.
///
/// This repository ships only the contract, the registration path, and the gate
/// (AGENTS.md Sec 7, architecture.md Sec 13). Concrete tradecraft -- recon,
/// lateral movement, persistence, collection, exfiltration, and any
/// evasion/exploit behavior -- is supplied as separate, opt-in, out-of-tree
/// modules that implement this interface. The built-in core-verb placeholders
/// exist only to keep every framework verb registered so the catalog lists the
/// full set and an out-of-tree module replacing one is a registration, not a
/// schema change.
/// </remarks>
public interface ICapabilityModule
{
    /// <summary>
    /// The capability this module provides. Registered with the
    /// <see cref="Registry.ICapabilityRegistry"/> so the task-issuance gate can
    /// admit the verb and a later module can replace the placeholder for it
    /// (last registration wins).
    /// </summary>
    CapabilityDescriptor Descriptor { get; }
}
