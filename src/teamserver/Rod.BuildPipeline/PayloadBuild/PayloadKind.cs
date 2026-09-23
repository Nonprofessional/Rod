namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// What a build request asks the unit to produce (architecture.md Sec 6).
/// The kind is the product axis -- which tier of the delivery stack the
/// artifact is -- while <see cref="ArtifactFormat"/> is the form factor of
/// the artifact itself. The implant is the full product; the loader is the
/// stage-0 tier, the small dialer whose own fetch delivers and runs the
/// implant tier from memory.
/// </summary>
public enum PayloadKind
{
    /// <summary>
    /// The full implant: every baked contact, the verb set, the sealed
    /// envelope -- the artifact the loaders and one-liners deliver.
    /// </summary>
    Implant = 0,

    /// <summary>
    /// The stage-0 loader (the Rust reference unit's second artifact): a
    /// no_std dialer that fetches the stage this build names, authenticates
    /// it under the build's per-artifact AES-GCM key (the stage seal), and
    /// execs it from a memfd. The build contract carries the stage
    /// reference; the fetch route serves the stage sealed under the key the
    /// loader bakes, so the pair lives exactly as long as the loader
    /// artifact does.
    /// </summary>
    Loader = 1,
}

/// <summary>
/// The wire names for <see cref="PayloadKind"/>: the operator-facing
/// spellings the build request accepts and the library view shows,
/// centralized so the parser, the recorder, and the responses cannot drift.
/// </summary>
public static class PayloadKinds
{
    /// <summary>TryParse accepting the wire names (case-insensitive); a null
    /// or empty text parses as the default implant kind so a minimal request
    /// stays valid.</summary>
    public static bool TryParse(string? text, out PayloadKind kind)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case null or "":
            case "implant":
                kind = PayloadKind.Implant;
                return true;
            case "loader":
                kind = PayloadKind.Loader;
                return true;
            default:
                kind = PayloadKind.Implant;
                return false;
        }
    }

    /// <summary>The wire name for a kind, as the request accepts it and the
    /// library shows.</summary>
    public static string Name(PayloadKind kind) => kind switch
    {
        PayloadKind.Implant => "implant",
        PayloadKind.Loader => "loader",
        _ => kind.ToString(),
    };
}
