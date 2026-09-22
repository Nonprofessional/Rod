namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The artifact form factor a build emits (architecture.md Sec 6). The build
/// contract carries it beside the class and target, and each build unit
/// interprets it for its toolchain; the values describe deployment shapes, not
/// compilation details, so a community unit in any language maps them to its
/// own closest equivalent.
/// </summary>
public enum ArtifactFormat
{
    /// <summary>
    /// The default executable spelling: the native binary cargo compiles for
    /// the requested target (ELF or PE), one self-contained file to drop and
    /// run. The name survives the retired .NET unit, whose single-file bundle
    /// it once named; the Rust unit builds this same artifact for every
    /// executable spelling.
    /// </summary>
    SingleFileExe = 0,

    /// <summary>
    /// A compatibility spelling, not a distinct Rust build: the unit emits
    /// the same native binary (the size posture lives in the crate's release
    /// profile -- opt-level, LTO, strip), so 'exe-trimmed' differs from
    /// 'exe' in name alone. Kept so requests written against the .NET-era
    /// contract keep parsing.
    /// </summary>
    TrimmedExe = 1,

    /// <summary>
    /// A compatibility spelling for the retired .NET unit's NativeAOT
    /// publish: byte-identical to 'exe' over the Rust unit. The in-memory
    /// delivery it once flagged is not a build-time axis anymore -- the
    /// launcher render offers the memfd family for every native payload.
    /// </summary>
    NativeAot = 2,

    /// <summary>
    /// Retired with the .NET implant it served: the managed zip bundle a
    /// .NET host loaded in-process. No unit produces it; the parser and the
    /// Rust unit both refuse it with the native spellings named.
    /// </summary>
    Dll = 3,
}

/// <summary>
/// The wire names for <see cref="ArtifactFormat"/>: the operator-facing spellings
/// the build request accepts and the library view shows. Shorter than the enum
/// names because operators type them; centralized so the parser, the recorder,
/// and the responses cannot drift.
/// </summary>
public static class ArtifactFormats
{
    /// <summary>TryParse accepting the wire names (case-insensitive); a null or
    /// empty text parses as the default single-file shape so a minimal request
    /// stays valid.</summary>
    public static bool TryParse(string? text, out ArtifactFormat format)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case null or "":
                format = ArtifactFormat.SingleFileExe;
                return true;
            case "exe":
                format = ArtifactFormat.SingleFileExe;
                return true;
            case "exe-trimmed":
                format = ArtifactFormat.TrimmedExe;
                return true;
            case "aot":
                format = ArtifactFormat.NativeAot;
                return true;
            case "dll":
                format = ArtifactFormat.Dll;
                return true;
            default:
                format = ArtifactFormat.SingleFileExe;
                return false;
        }
    }

    /// <summary>The wire name for a format, as the request accepts it and the
    /// library shows it.</summary>
    public static string Name(ArtifactFormat format) => format switch
    {
        ArtifactFormat.SingleFileExe => "exe",
        ArtifactFormat.TrimmedExe => "exe-trimmed",
        ArtifactFormat.NativeAot => "aot",
        ArtifactFormat.Dll => "dll",
        _ => format.ToString(),
    };
}
