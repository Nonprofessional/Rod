namespace Rod.CoreState.Implants;

/// <summary>
/// The static-fallback <see cref="IVerbOutputResolver"/>: the built-in verbs
/// whose output grammar the read-side projections parse, mirrored here so a
/// host that never opts into the capabilities layer (the transport test host)
/// keeps parsing them. The registry-backed implementation answers the same
/// question from each verb's descriptor and covers every registered verb;
/// this floor exists only for the hosts that run without it.
/// </summary>
public sealed class DefaultVerbOutputResolver : IVerbOutputResolver
{
    private static readonly IReadOnlyDictionary<string, TaskOutputKind> BuiltIn = new Dictionary<string, TaskOutputKind>(StringComparer.OrdinalIgnoreCase)
    {
        // The recon findings grammar the topology projection parses.
        ["recon.portscan"] = TaskOutputKind.Enumeration,
        ["recon.hostenum"] = TaskOutputKind.Enumeration,

        // The credential-row grammar the credentials projection parses.
        ["collect.cred"] = TaskOutputKind.Credential,

        // Bulk that rides the exfil chunk path into the artifact store.
        ["file.pull"] = TaskOutputKind.File,
        ["exfil.push"] = TaskOutputKind.File,

        // The captured screens.
        ["collect.screenshot"] = TaskOutputKind.Screenshot,
        ["browser.screenshot"] = TaskOutputKind.Screenshot,
    };

    public TaskOutputKind? OutputKind(string verb)
        => BuiltIn.TryGetValue(verb, out var kind) ? kind : null;
}
