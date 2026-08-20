using Rod.CoreState.Engagements;

namespace Rod.CoreState.Implants;

/// <summary>
/// The reduced capability verb set each <see cref="ImplantClass"/> may run
/// (architecture.md Sec 5.2). Implants differ by operational purpose, not by a
/// "device flavor": a stager only fetches, a web-shell executes over HTTP but
/// holds no tunnel, an ephemeral is one-shot, and a pivot forwards traffic for
/// hosts that cannot run their own implant. A stage-2 implant carries the full
/// core set plus the tunnel set, the recon set, the lateral set, the persist
/// set, the collect set, and the exfil set; tunneling joins stage-2's core
/// operations, and recon, lateral movement, persistence, collection,
/// and exfiltration are long-haul activities. These sets are
/// the server's authority for what a class is allowed
/// to do: task issuance gates on them (architecture.md Sec 10.3), and the build
/// pipeline bakes them into each artifact so the generated payload is
/// self-describing.
/// </summary>
/// <remarks>
/// The values live in core state -- the inner ring both the build pipeline and
/// the tradecraft layer may depend on -- so the rule is defined once and read by
/// every layer that needs it, without a cross-layer dependency. The standard,
/// documented categories run concrete handlers on the reference implant; the
/// sensitive categories (Sec 13) stay contract-only in this repository.
/// </remarks>
public static class ImplantClassCapabilities
{
    /// <summary>
    /// The verbs a class of implant is permitted to run. Stage-2 carries the
    /// full core set plus the recon set, the lateral set, the persist set, the
    /// collect set, and the exfil set; every other class carries the subset its
    /// operational purpose justifies (architecture.md Sec 5.2). Stored read-only
    /// and case-normalized so <see cref="Allows"/> can match case-insensitively
    /// without re-allocating.
    /// </summary>
    private static readonly IReadOnlyDictionary<ImplantClass, IReadOnlyList<string>> ByClass =
        new Dictionary<ImplantClass, IReadOnlyList<string>>
        {
            // The primary long-haul implant: the whole core baseline plus the
            // tunnel set, the recon set, the lateral set, the persist set, the
            // collect set, and the exfil set (architecture.md Sec 10.1, Sec 14).
            // Tunneling is a core operation, and recon, lateral movement,
            // persistence, collection, and exfiltration are all long-haul
            // activities that justify a stage-2 footprint and no other class.
            [ImplantClass.Stage2] = new[]
            {
                "shell.exec", "shell.interact", "file.push", "file.pull", "proc.kill",
                "tunnel.forward", "tunnel.socks",
                "recon.portscan", "recon.hostenum", "recon.service", "recon.ps",
                "lateral.move", "lateral.token", "lateral.exec_remote",
                "persist.install", "persist.remove", "persist.list",
                "collect.cred", "collect.keylog", "collect.screenshot",
                "exfil.push", "exfil.stage",
            },

            // A tiny stage-1 loader: it only pulls the stage-2 payload it then
            // hands off to (architecture.md Sec 5.2).
            [ImplantClass.Stager] = new[] { "file.pull" },

            // A script in a web root: code execution over HTTP, no file transfer
            // and no interactive PTY.
            [ImplantClass.WebShell] = new[] { "shell.exec" },

            // A short-lived, TTL'd implant from a one-liner bootstrap: enough for
            // one-off execution and nothing more.
            [ImplantClass.Ephemeral] = new[] { "shell.exec" },

            // The tunneling class: an artifact that represents hosts which
            // cannot run their own implant (network/OT gear) and forwards their
            // traffic (architecture.md Sec 5.2). It carries exactly the tunnel
            // set -- a pivot forwards, it does not shell -- so a Pivot-class
            // build is the minimal tunneling artifact and nothing else.
            [ImplantClass.Pivot] = new[] { "tunnel.forward", "tunnel.socks" },
        };

    /// <summary>
    /// The verbs <paramref name="class"/> is permitted to run, in declared
    /// order. Returned read-only so callers cannot mutate the shared set.
    /// </summary>
    public static IReadOnlyCollection<string> For(ImplantClass @class)
        => ByClass[@class];

    /// <summary>
    /// The contract-only verbs no class gates (architecture.md Sec 5.2, Sec
    /// 10.2): the evasion and exploit categories in their entirety. Which class
    /// runs one is decided when an operator deploys the out-of-tree module, not
    /// by a baked-in class rule, so they sit outside the per-class table. The
    /// build pipeline bakes them alongside the class set so an artifact that
    /// carries an out-of-tree handler advertises the verb at handshake; the
    /// advertised set is still the baked verbs intersected with the compiled
    /// handlers (architecture.md Sec 5.3), so an artifact without the handler
    /// never claims the verb.
    /// </summary>
    public static readonly IReadOnlyList<string> Ungated = new[]
    {
        "evasion.avoid", "evasion.unload", "exploit.invoke", "exploit.module",
    };

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
