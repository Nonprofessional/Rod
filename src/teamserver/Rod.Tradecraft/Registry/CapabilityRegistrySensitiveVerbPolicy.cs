using Rod.CoreState.Automation;
using Rod.CoreState.Implants;
using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Registry;

/// <summary>
/// The registry-backed <see cref="ISensitiveVerbPolicy"/> (architecture.md
/// Sec 10.4, the guards): a verb is sensitive when its registered
/// <see cref="CapabilityDescriptor"/> says so -- it sits in one of the two
/// contract-only categories (Evasion, Exploit) or carries one of the OPSEC
/// attributes that mean "never unattended" (input capture, process-memory
/// reads, code injection, defense modification). The composition root swaps
/// this in over core state's static fallback.
/// </summary>
/// <remarks>
/// <para>
/// The judgment rides the descriptor a module already registers, so a new
/// verb -- the framework's or an out-of-tree module's -- acquires its
/// automation posture the moment it acquires its OPSEC metadata; no parallel
/// list exists to drift. The attribute set below is the contract between
/// this policy and module authors: declare one of these attributes and the
/// verb never fires unattended, whatever its category.
/// </para>
/// <para>
/// The static fallback (<see cref="AutomationLimits.IsSensitiveVerb"/>) stays
/// loaded underneath and the two OR together: the registry may only widen
/// the sensitive set, never narrow it -- a listed verb stays sensitive even
/// if some module registers a milder descriptor for it. The direction is the
/// registry-backed capability resolver's rule on the dispatch axis, in
/// reverse.
/// </para>
/// </remarks>
public sealed class CapabilityRegistrySensitiveVerbPolicy : ISensitiveVerbPolicy
{
    /// <summary>
    /// The OPSEC attributes that make a verb sensitive for unattended firing.
    /// Each one names what the verb does to the target that a human must
    /// witness: keystrokes, process memory, injected code, defensive posture.
    /// </summary>
    private static readonly HashSet<string> SensitiveAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "reads-input",
        "reads-memory",
        "executes-code",
        "modifies-defenses",
    };

    private readonly ICapabilityRegistry _registry;

    public CapabilityRegistrySensitiveVerbPolicy(ICapabilityRegistry registry)
    {
        _registry = registry;
    }

    public bool IsSensitive(string verb)
        => AutomationLimits.IsSensitiveVerb(verb)
            || _registry.FindAsync(verb).GetAwaiter().GetResult() is { } module
                && IsSensitiveDescriptor(module.Descriptor);

    internal static bool IsSensitiveDescriptor(CapabilityDescriptor descriptor)
        => descriptor.Category is CapabilityCategory.Evasion or CapabilityCategory.Exploit
            || descriptor.Attributes.Keys.Any(SensitiveAttributes.Contains);
}
