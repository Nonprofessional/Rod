using Rod.CoreState.Engagements;

namespace Rod.CoreState.Implants;

/// <summary>
/// The reduced capability verb set each <see cref="ImplantClass"/> may run
/// (architecture.md Sec 5.2). Implants differ by operational purpose, not by a
/// "device flavor": a stager only fetches, a web-shell executes over HTTP but
/// holds no tunnel, an ephemeral is one-shot, and a pivot forwards tasking for
/// hosts that cannot run their own implant. A stage-2 implant carries the full
/// core set plus the recon set and the lateral set; recon and lateral movement
/// are long-haul activities. These sets are the server's authority for what a
/// class is allowed
/// to do: task issuance gates on them (architecture.md Sec 10.3), and the build
/// pipeline bakes them into each artifact so the generated payload is
/// self-describing.
/// </summary>
/// <remarks>
/// The values live in core state -- the inner ring both the build pipeline and
/// the tradecraft layer may depend on -- so the rule is defined once and read by
/// every layer that needs it, without a cross-layer dependency. Concrete verb
/// behavior stays out of this repository (RESPONSIBLE-USE.md, AGENTS.md Sec 7):
/// this is the contract, not the tradecraft.
/// </remarks>
public static class ImplantClassCapabilities
{
    /// <summary>
    /// The verbs a class of implant is permitted to run. Stage-2 carries the
    /// full core set plus the recon set and the lateral set; every other class
    /// carries the subset its operational purpose justifies (architecture.md
    /// Sec 5.2). Stored read-only and case-normalized so <see cref="Allows"/> can
    /// match case-insensitively without re-allocating.
    /// </summary>
    private static readonly IReadOnlyDictionary<ImplantClass, IReadOnlyList<string>> ByClass =
        new Dictionary<ImplantClass, IReadOnlyList<string>>
        {
            // The primary long-haul implant: the whole core baseline plus the
            // recon set and the lateral set (architecture.md Sec 10.1). Recon is
            // a long-haul activity, and lateral movement derives child implants
            // and pivots within scope -- both justify a stage-2 footprint and no
            // other class.
            [ImplantClass.Stage2] = new[]
            {
                "shell.exec", "file.push", "file.pull", "tunnel.open", "probe.read",
                "recon.portscan", "recon.hostenum", "recon.service",
                "lateral.move", "lateral.token", "lateral.exec_remote",
            },

            // A tiny stage-1 loader: it only pulls the stage-2 payload it then
            // hands off to (architecture.md Sec 5.2).
            [ImplantClass.Stager] = new[] { "file.pull" },

            // A script in a web root: code execution over HTTP, no interactive
            // PTY and no long-lived tunnel.
            [ImplantClass.WebShell] = new[] { "shell.exec", "probe.read" },

            // A short-lived, TTL'd implant from a one-liner bootstrap: enough for
            // one-off execution and a quick read.
            [ImplantClass.Ephemeral] = new[] { "shell.exec", "probe.read" },

            // A host that cannot run its own implant: it forwards tasking and
            // tunnels traffic, so it carries the tunnel/probe verbs and no shell.
            [ImplantClass.Pivot] = new[] { "tunnel.open", "probe.read" },
        };

    /// <summary>
    /// The verbs <paramref name="class"/> is permitted to run, in declared
    /// order. Returned read-only so callers cannot mutate the shared set.
    /// </summary>
    public static IReadOnlyCollection<string> For(ImplantClass @class)
        => ByClass[@class];

    /// <summary>
    /// Whether <paramref name="class"/> may run <paramref name="verb"/>. Verb
    /// matching is case-insensitive: capability verbs are namespaced strings
    /// (<c>namespace.action</c>) and the registry already resolves them that way.
    /// An empty or whitespace verb is never allowed -- the task entity requires a
    /// verb, and a class advertising nothing cannot run a blank one.
    /// </summary>
    public static bool Allows(ImplantClass @class, string? verb)
    {
        if (string.IsNullOrWhiteSpace(verb))
            return false;

        var set = ByClass[@class];
        for (var i = 0; i < set.Count; i++)
        {
            if (string.Equals(set[i], verb, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
