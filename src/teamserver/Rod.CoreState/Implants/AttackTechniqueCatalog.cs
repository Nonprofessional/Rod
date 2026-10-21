using System.Reflection;
using System.Text.Json;

namespace Rod.CoreState.Implants;

/// <summary>
/// One ATT&CK technique the verb-to-technique mapping references
/// (architecture.md Sec 15.3, ADR-007): its id, display name, and the enterprise
/// tactics it falls under (short names, e.g. <c>credential-access</c>). The
/// catalog carries display metadata for techniques the mapping already names --
/// never a taxonomy of what may exist (architecture.md Sec 13).
/// </summary>
public sealed record AttackTechnique(string Id, string Name, IReadOnlyList<string> Tactics);

/// <summary>
/// The kernel's verb-to-technique mapping, loaded from the embedded
/// <c>attack-coverage.json</c> data file (architecture.md Sec 15.3, ADR-007):
/// for every framework verb the ATT&CK techniques it covers, and for every
/// referenced technique its name and tactics. This is the file a coverage
/// matrix, a report's Navigator layer, and a gap queue all read as their one
/// source -- modules extend it at runtime by declaring their own techniques
/// through the registry-backed resolver, never by editing the file.
/// </summary>
/// <remarks>
/// A verb absent from the <c>verbs</c> half is simply unmapped -- proc.kill and
/// beacon.sleep have no honest technique, and plumbing verbs (the plugin seam's
/// own family) are not tradecraft. The file is data, not a gate: nothing in the
/// task path consults it. Load is lazy and validates every invariant below,
/// throwing loudly at first use (startup in practice) so a bad edit to the file
/// fails the process rather than quietly skewing the matrix.
/// </remarks>
public static class AttackTechniqueCatalog
{
    /// <summary>
    /// The ATT&CK version the catalog's ids, names, and tactic assignments
    /// were checked against. The Navigator layer export stamps it so the
    /// rendered layer matches the data that produced it.
    /// </summary>
    public static string AttackVersion => Data.Value.AttackVersion;

    /// <summary>Every referenced technique, ordered by id.</summary>
    public static IReadOnlyList<AttackTechnique> Techniques => Data.Value.Techniques;

    /// <summary>
    /// The technique with <paramref name="id"/>, or null when the catalog does
    /// not reference it. Case-insensitive on the id.
    /// </summary>
    public static AttackTechnique? Find(string id)
        => Data.Value.ById.TryGetValue(id, out var technique) ? technique : null;

    /// <summary>
    /// The mapped verbs with their technique ids, keyed case-insensitively.
    /// The mapping for each verb replaces nothing and merges with nothing --
    /// it is the whole kernel entry for that verb, which is exactly the
    /// replace semantics a module's declaration rides (last registration
    /// wins, the same rule the capability registry holds).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Verbs => Data.Value.ByVerb;

    private static readonly Lazy<CatalogData> Data = new(Load, LazyThreadSafetyMode.PublicationOnly);

    private sealed record CatalogData(
        string AttackVersion,
        IReadOnlyList<AttackTechnique> Techniques,
        IReadOnlyDictionary<string, AttackTechnique> ById,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ByVerb);

    private sealed record CatalogFile(
        string? AttackVersion,
        Dictionary<string, string[]>? Verbs,
        Dictionary<string, TechniqueFile>? Techniques);

    private sealed record TechniqueFile(string? Name, string[]? Tactics);

    private const string ResourceName = "Rod.CoreState.Implants.attack-coverage.json";

    private static CatalogData Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded ATT&CK coverage data '{ResourceName}' is missing from {assembly.GetName().Name}.");

        var file = JsonSerializer.Deserialize<CatalogFile>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new InvalidOperationException($"The embedded ATT&CK coverage data '{ResourceName}' is empty.");

        if (string.IsNullOrWhiteSpace(file.AttackVersion))
            throw new InvalidOperationException("attack-coverage.json: attackVersion is required.");

        var byId = new Dictionary<string, AttackTechnique>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, technique) in file.Techniques ?? [])
        {
            ValidateId(id, "techniques");
            if (string.IsNullOrWhiteSpace(technique.Name))
                throw new InvalidOperationException($"attack-coverage.json: technique '{id}' needs a name.");
            if (technique.Tactics is not { Length: > 0 })
                throw new InvalidOperationException($"attack-coverage.json: technique '{id}' needs at least one tactic.");
            if (!byId.TryAdd(id, new AttackTechnique(id, technique.Name, technique.Tactics)))
                throw new InvalidOperationException($"attack-coverage.json: technique '{id}' is declared twice.");
        }

        var byVerb = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (verb, ids) in file.Verbs ?? [])
        {
            if (string.IsNullOrWhiteSpace(verb))
                throw new InvalidOperationException("attack-coverage.json: a verbs entry has a blank verb.");
            if (ids is not { Length: > 0 })
                throw new InvalidOperationException($"attack-coverage.json: verb '{verb}' maps to no technique; drop the entry instead.");
            foreach (var id in ids)
            {
                ValidateId(id, $"verbs.{verb}");
                if (!byId.ContainsKey(id))
                    throw new InvalidOperationException(
                        $"attack-coverage.json: verb '{verb}' maps to '{id}', which the techniques half does not declare.");
            }
            if (!byVerb.TryAdd(verb, ids))
                throw new InvalidOperationException($"attack-coverage.json: verb '{verb}' is declared twice.");
        }

        return new CatalogData(
            file.AttackVersion,
            byId.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray(),
            byId,
            byVerb);
    }

    private static void ValidateId(string id, string where)
    {
        // T#### or T####.###, nothing else: pinned digit counts, so "T105" or
        // "T1.050" cannot slip through where uint.TryParse would accept them.
        var valid = id is { Length: 5 or 9 }
            && id[0] == 'T'
            && Digits(id.Substring(1, 4))
            && (id.Length == 5 || (id[5] == '.' && Digits(id.Substring(6, 3))));
        if (!valid)
            throw new InvalidOperationException(
                $"attack-coverage.json: '{id}' ({where}) is not an ATT&CK technique id (T#### or T####.###).");
    }

    private static bool Digits(string s) => s.All(char.IsDigit);
}
