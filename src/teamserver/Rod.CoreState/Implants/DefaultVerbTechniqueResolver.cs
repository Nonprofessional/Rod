namespace Rod.CoreState.Implants;

/// <summary>
/// The static fallback <see cref="IVerbTechniqueResolver"/>: every answer
/// comes from the kernel's <c>attack-coverage.json</c> data file
/// (architecture.md Sec 15.3, ADR-007), so a host with no capability modules
/// loaded -- the transport test host, a report unit test -- still resolves the
/// full framework mapping. The composed teamserver swaps this for the
/// registry-backed resolver at the composition root.
/// </summary>
public sealed class DefaultVerbTechniqueResolver : IVerbTechniqueResolver
{
    /// <inheritdoc />
    public IReadOnlyList<string>? Techniques(string verb)
        => AttackTechniqueCatalog.Verbs.TryGetValue(verb, out var techniques) ? techniques : null;
}
