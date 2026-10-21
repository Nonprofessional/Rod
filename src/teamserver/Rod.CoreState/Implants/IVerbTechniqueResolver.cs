namespace Rod.CoreState.Implants;

/// <summary>
/// Answers a verb's ATT&CK techniques (architecture.md Sec 15.3, ADR-007).
/// The two consumer faces -- the coverage matrix ("what can be done") and the
/// report's Navigator layer ("what was done") -- both read this one answer, so
/// they can never disagree about a verb's mapping.
/// </summary>
/// <remarks>
/// The port lives in core state (the inner ring every layer may reach) and
/// ships with the static fallback <see cref="DefaultVerbTechniqueResolver"/>,
/// which reads the kernel's <c>attack-coverage.json</c> data file. The
/// authoritative implementation is registry-backed and lives in the
/// capabilities layer, where a module's descriptor carries its own technique
/// declarations and replaces the kernel's entry for its verb (last
/// registration wins); the composition root swaps it in, the same
/// replace-the-default shape as <see cref="IVerbOutputResolver"/>,
/// <see cref="ITaskCapabilityResolver"/>, and <see cref="ISensitiveVerbPolicy"/>.
/// </remarks>
public interface IVerbTechniqueResolver
{
    /// <summary>
    /// The technique ids mapped for <paramref name="verb"/>, or null when no
    /// mapping exists -- an unmapped verb (proc.kill, the plugin-seam plumbing)
    /// is unrepresented on the matrix by design, not an error.
    /// </summary>
    IReadOnlyList<string>? Techniques(string verb);
}
