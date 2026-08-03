namespace Rod.Tradecraft.Capabilities;

/// <summary>
/// The declaration of one capability a module provides (architecture.md Sec 10):
/// a namespaced verb (<c>namespace.action</c>, e.g. <c>shell.exec</c>), its
/// <see cref="Category"/>, a <see cref="Version"/>, and a free-form set of
/// <see cref="Attributes"/>. The teamserver gates dispatch on the verb an implant
/// advertises; the descriptor is what a module registers so the dispatcher knows
/// it exists and where to route it.
/// </summary>
/// <remarks>
/// <see cref="Attributes"/> is the per-command OPSEC metadata surface
/// (architecture.md Sec 7): flags such as "writes to disk" that let operators and
/// tradecraft filters avoid risky actions. This layer holds only the shape; the
/// concrete attributes a real module sets arrive with that module.
/// </remarks>
public sealed record CapabilityDescriptor(
    string Verb,
    CapabilityCategory Category,
    string Version,
    IReadOnlyDictionary<string, string> Attributes)
{
    /// <summary>
    /// Builds a descriptor with no attributes. Most stub/core verbs carry no
    /// OPSEC metadata yet; this keeps their registration readable.
    /// </summary>
    public static CapabilityDescriptor Of(
        string verb,
        CapabilityCategory category,
        string version,
        IReadOnlyDictionary<string, string>? attributes = null)
        => new(
            verb,
            category,
            version,
            attributes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
