using Rod.CoreState.Implants;

namespace Rod.Tradecraft.Registry;

/// <summary>
/// The capability-registry-backed <see cref="ITaskCapabilityResolver"/>
/// (architecture.md Sec 5.2/10.2/10.3). The composition root swaps this in for
/// core state's strict class-table default so the live task path resolves verbs
/// through the capability registry in addition to the per-class reduced set.
/// </summary>
/// <remarks>
/// <para>
/// The class table stays the primary authority: a verb in the implant's class
/// set is always dispatchable. Only a verb the class set does not admit falls
/// through to the registry -- the path that opens dispatch for the
/// contract-and-dispatch-only categories (architecture.md Sec 10.2). Evasion and
/// exploit are not class-gated (Sec 5.2/10.1); they are dispatchable here when a
/// module is registered for the verb, which the built-in load guarantees for
/// every framework verb and an out-of-tree override keeps.
/// </para>
/// <para>
/// A registered module -- the built-in placeholder or an operator-supplied
/// out-of-tree module -- is what satisfies the gate. The placeholder represents
/// "the framework knows this verb"; concrete behavior runs on the implant
/// (architecture.md Sec 10.3) and is supplied out-of-tree for the sensitive
/// categories (Sec 13, AGENTS.md Sec 7). This layer holds the contract and the
/// dispatch path, never the tradecraft.
/// </para>
/// <para>
/// Lives in the tradecraft layer (Layer 6, may depend on core state) so core
/// state stays inward-only: it defines the port, this layer implements it.
/// </para>
/// </remarks>
public sealed class CapabilityRegistryTaskResolver : ITaskCapabilityResolver
{
    private readonly ICapabilityRegistry _registry;

    public CapabilityRegistryTaskResolver(ICapabilityRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// True when <paramref name="verb"/> is in <paramref name="class"/>'s reduced
    /// verb set, or a capability module is registered for it. The class table is
    /// checked first (short-circuit, no allocation); only verbs it does not admit
    /// consult the registry.
    /// </summary>
    public bool IsDispatchable(ImplantClass @class, string verb)
        => ImplantClassCapabilities.Allows(@class, verb)
            || _registry.FindAsync(verb).GetAwaiter().GetResult() is not null;
}
