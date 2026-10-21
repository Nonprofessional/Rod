namespace Rod.CoreState.Implants;

/// <summary>
/// What a task verb's results carry, from the closed taxonomy of the contract
/// trilogy's second contract (architecture.md Sec 15.2). This is the
/// core-state mirror of the capabilities layer's declaration: the same seven
/// kinds, spelled in the inner ring so the read-side projections (the topology
/// parse, the loot classification, the credentials view) can ask for a verb's
/// kind without referencing the layer that owns the descriptors.
/// </summary>
public enum TaskOutputKind
{
    /// <summary>Plain text; the default and the degradation target.</summary>
    Text,

    /// <summary>Recon findings in the documented JSON-lines grammar.</summary>
    Enumeration,

    /// <summary>Process rows in the documented JSON-lines grammar.</summary>
    ProcessList,

    /// <summary>Directory entries in the documented JSON-lines grammar.</summary>
    FileList,

    /// <summary>Credential rows in the documented JSON-lines grammar.</summary>
    Credential,

    /// <summary>A produced file artifact via the exfil chunk path.</summary>
    File,

    /// <summary>A captured screen as a PNG artifact.</summary>
    Screenshot,
}

/// <summary>
/// Answers a verb's declared output kind (architecture.md Sec 15.2, the
/// contract trilogy's second contract). The read-side projections consult
/// this one answer instead of hardcoding verb names, so a module's
/// structured output reaches the topology, loot, and credentials panels the
/// moment its descriptor declares the kind -- zero framework change.
/// </summary>
/// <remarks>
/// The port lives in core state (the inner ring every layer may reach) and
/// ships with the static fallback <see cref="DefaultVerbOutputResolver"/>,
/// which mirrors the built-in verbs the read-side projections parse. The
/// authoritative implementation is registry-backed and lives in the
/// capabilities layer, where each verb's descriptor carries the declaration;
/// the composition root swaps it in, the same replace-the-default shape as
/// <see cref="ITaskCapabilityResolver"/> and <see cref="ISensitiveVerbPolicy"/>.
/// </remarks>
public interface IVerbOutputResolver
{
    /// <summary>
    /// The declared output kind for <paramref name="verb"/>, or null when no
    /// declaration exists -- a verb nobody described renders as plain text,
    /// which is the honest degradation, not an error.
    /// </summary>
    TaskOutputKind? OutputKind(string verb);
}
