namespace Rod.CoreState.Engagements;

/// <summary>
/// The engagement's rules-of-engagement profile (architecture.md Sec 9 -- ROE
/// guardrails): the server-side scope of what is taskable within this
/// engagement. Two independent allow-list dimensions, each empty meaning
/// unrestricted -- a set profile narrows, and anything not listed is refused:
/// <c>PermittedVerbs</c> gates which capability verbs may be tasked (exact
/// verb or a <c>namespace.*</c> wildcard), <c>PermittedImplants</c> gates
/// which implants may be tasked (exact implant id). Enforcement is at task
/// issuance, before the task is queued; the refusal is audited naming the
/// violated rule. Pure server-side scope -- the implant contract carries
/// nothing for it (extending/implants.md, evolution rule 4).
/// </summary>
public sealed record RoeProfile
{
    /// <summary>No scope: every verb taskable, every implant taskable.</summary>
    public static readonly RoeProfile Unrestricted = new([], []);

    public IReadOnlyList<string> PermittedVerbs { get; }
    public IReadOnlyList<string> PermittedImplants { get; }

    public RoeProfile(
        IEnumerable<string>? permittedVerbs,
        IEnumerable<string>? permittedImplants)
    {
        PermittedVerbs = Normalize(permittedVerbs);
        PermittedImplants = Normalize(permittedImplants);
    }

    /// <summary>
    /// Evaluates a prospective task against the profile. Returns null when the
    /// task is inside scope; otherwise the violated rule, phrased for the
    /// refusal and the audit entry that names it.
    /// </summary>
    public string? Evaluate(string implantId, string verb)
    {
        if (PermittedVerbs.Count > 0 && !PermittedVerbs.Any(p => VerbMatches(p, verb)))
            return $"verb '{verb}' is outside the engagement's ROE permitted verbs";
        if (PermittedImplants.Count > 0 && !PermittedImplants.Contains(implantId))
            return $"implant '{implantId}' is outside the engagement's ROE permitted targets";
        return null;
    }

    // A pattern ending in ".*" admits the whole namespace; anything else is an
    // exact verb. No general globbing -- the two shapes cover ROE scope
    // without a pattern engine.
    private static bool VerbMatches(string pattern, string verb)
        => pattern.EndsWith(".*", StringComparison.Ordinal)
            ? verb.StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.Ordinal)
            : string.Equals(pattern, verb, StringComparison.Ordinal);

    private static IReadOnlyList<string> Normalize(IEnumerable<string>? values)
        => (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
