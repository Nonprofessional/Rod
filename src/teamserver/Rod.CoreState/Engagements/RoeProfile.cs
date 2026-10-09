using System.Net;

namespace Rod.CoreState.Engagements;

/// <summary>
/// The engagement's rules-of-engagement profile (architecture.md Sec 9 --
/// ROE guardrails): the server-side scope of what the engagement may act
/// against. Three independent allow-list dimensions, each empty meaning
/// unrestricted -- a set profile narrows, and anything not listed is refused:
/// <c>PermittedVerbs</c> gates which capability verbs may be tasked (exact
/// verb or a <c>namespace.*</c> wildcard), <c>PermittedImplants</c> gates
/// which implants may be tasked (exact implant id), and
/// <c>PermittedTargets</c> gates which external targets the recon
/// workbench may act on (exact hostname case-folded, exact IP literal, or
/// CIDR block; see <see cref="EvaluateTarget"/>). Tasking enforcement is at
/// task issuance, before the task is queued; target enforcement is at the
/// workbench's scan, before a connection is opened. Each refusal is audited
/// naming the violated rule. Pure server-side scope -- the implant contract
/// carries nothing for it (extending/implants.md, evolution rule 4).
/// </summary>
public sealed record RoeProfile
{
    /// <summary>No scope: every verb taskable, every implant taskable, every target addressable.</summary>
    public static readonly RoeProfile Unrestricted = new([], [], []);

    public IReadOnlyList<string> PermittedVerbs { get; }
    public IReadOnlyList<string> PermittedImplants { get; }
    public IReadOnlyList<string> PermittedTargets { get; }

    public RoeProfile(
        IEnumerable<string>? permittedVerbs,
        IEnumerable<string>? permittedImplants,
        IEnumerable<string>? permittedTargets = null)
    {
        PermittedVerbs = Normalize(permittedVerbs);
        PermittedImplants = Normalize(permittedImplants);
        PermittedTargets = Normalize(permittedTargets);
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
            return $"implant '{implantId}' is outside the engagement's ROE permitted implants";
        return null;
    }

    /// <summary>
    /// Evaluates an external target the recon workbench names (architecture.md
    /// Sec 11.4) against the profile's target dimension. Returns null when the
    /// target is inside scope; otherwise the violated rule, phrased for the
    /// refusal and the audit entry that names it. A hostname target matches an
    /// entry by name only, never by resolution -- a DNS answer is not an
    /// authorization fact, so the scope never depends on what the target's own
    /// nameservers say. An IP target matches an identical entry or an entry
    /// whose network contains it.
    /// </summary>
    public string? EvaluateTarget(string target)
    {
        if (PermittedTargets.Count == 0)
            return null;

        var trimmed = target.Trim();
        if (PermittedTargets.Any(entry => TargetMatches(entry, trimmed)))
            return null;
        return $"target '{trimmed}' is outside the engagement's ROE permitted targets";
    }

    /// <summary>
    /// Whether an entry parses as this dimension intends: hostnames and IP
    /// literals always do; an entry naming a network (anything with a
    /// <c>/</c>) must parse as one, so a mistyped CIDR is refused at the
    /// apply route instead of sitting in the profile matching nothing --
    /// for a gate that blocks, a rule that silently never matches is a
    /// misconfiguration, not safety.
    /// </summary>
    public static bool IsValidTargetEntry(string entry)
        => entry.Contains('/') ? IPNetwork.TryParse(entry, out _) : true;

    // An IP target matches an identical entry or a containing network entry;
    // a hostname target matches only an identical (case-folded) entry. An
    // entry with a "/" that does not parse as a network was rejected at the
    // apply route, so here it simply matches nothing.
    private static bool TargetMatches(string entry, string target)
    {
        if (IPAddress.TryParse(target, out var address))
        {
            if (IPAddress.TryParse(entry, out var exact))
                return exact.Equals(address);
            return IPNetwork.TryParse(entry, out var network) && network.Contains(address);
        }

        return !entry.Contains('/') && string.Equals(entry, target, StringComparison.OrdinalIgnoreCase);
    }

    // A pattern ending in ".*" admits the whole namespace; anything else is
    // an exact verb. No general globbing -- the two shapes cover ROE scope
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
