using System.Collections.Concurrent;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// In-memory <see cref="IBuildUnitRegistry"/> for the walking skeleton. Build
/// units live in a process-local dictionary keyed by language; one unit per
/// language, last registration wins. No lock is needed: registration happens at
/// startup before any build request, and the dictionary is read-mostly
/// thereafter (the same shape as the other read-mostly in-memory adapters).
/// </summary>
public sealed class InMemoryBuildUnitRegistry : IBuildUnitRegistry
{
    private readonly ConcurrentDictionary<Language, IBuildUnit> _units = new();

    public void Register(IBuildUnit unit) => _units[unit.Language] = unit;

    public IBuildUnit? Find(Language language)
        => _units.TryGetValue(language, out var unit) ? unit : null;
}
