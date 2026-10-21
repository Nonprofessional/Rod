namespace Rod.Capabilities;

/// <summary>
/// What a verb's task results carry, from the closed taxonomy of the contract
/// trilogy's second contract (architecture.md Sec 15.2). The declaration is
/// metadata for the generic panels -- the credential, file, and process views
/// render over any verb that declares the kind, and a verb whose results do
/// not parse against the kind's grammar degrades to plain text: the UI keeps
/// working, the module just earns no panels.
/// </summary>
public enum VerbOutputKind
{
    /// <summary>Plain text; the default and the degradation target. No
    /// panel beyond the transcript itself.</summary>
    Text,

    /// <summary>Recon findings in the documented JSON-lines grammar (one
    /// object per line, <c>host</c> required): hosts, ports, addresses. The
    /// topology view parses these.</summary>
    Enumeration,

    /// <summary>Process rows in the documented JSON-lines grammar (pid, ppid,
    /// user, image). The process view parses these.</summary>
    ProcessList,

    /// <summary>Directory entries in the documented JSON-lines grammar (name,
    /// dir, size, mtime). The file view parses these.</summary>
    FileList,

    /// <summary>Credential rows in the documented JSON-lines grammar (kind,
    /// principal, source, optional secret). The credentials view parses
    /// these.</summary>
    Credential,

    /// <summary>A produced file: the result's bulk rides the exfil chunk path
    /// into the engagement artifact store, joined to its task. Classifies as
    /// file loot.</summary>
    File,

    /// <summary>A captured screen: a PNG artifact through the exfil chunk
    /// path. Classifies as screenshot loot and renders inline.</summary>
    Screenshot,
}

/// <summary>
/// A verb's declared output (architecture.md Sec 15.2): the taxonomy kind
/// plus one sentence on what the verb's results hold. The grammar behind each
/// row kind is the documented JSON-lines shape; the declaration names which
/// one a verb speaks.
/// </summary>
public sealed record VerbOutput(VerbOutputKind Kind, string? Help = null)
{
    public static readonly VerbOutput Plain = new(VerbOutputKind.Text);
}
