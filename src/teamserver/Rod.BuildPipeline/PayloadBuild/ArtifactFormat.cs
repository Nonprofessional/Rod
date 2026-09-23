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
    /// A shared library a host process or loader maps in-process: the PE
    /// spelling on Windows. The name once flagged the retired .NET unit's
    /// managed zip bundle -- nothing produces or consumes that shape
    /// anymore, and the parser refused it -- so the wire name returns as
    /// the native deployment shape it now names. Not yet a build output of
    /// the in-tree Rust unit: the contract carries it for the loader and
    /// injection deliveries that load a stage without exec, and the unit
    /// refuses it naming that state until the toolchain work lands.
    /// </summary>
    Dll = 3,

    /// <summary>
    /// A position-independent code blob an injector writes into a process
    /// and jumps to: no container, no headers, the bytes alone. The shape
    /// <c>inject.shellcode</c> spends on the target. Not yet a build output
    /// of the in-tree Rust unit; the contract carries it so a build request
    /// and a library row can name the delivery before the transform that
    /// produces it arrives.
    /// </summary>
    Shellcode = 4,

    /// <summary>
    /// An ELF shared object a host process or loader maps in-process: the
    /// Unix spelling of <see cref="Dll"/>. Not yet a build output of the
    /// in-tree Rust unit; the contract carries it for the loader and
    /// injection deliveries on Unix targets.
    /// </summary>
    SharedObject = 5,
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
            case "shellcode":
                format = ArtifactFormat.Shellcode;
                return true;
            case "so":
                format = ArtifactFormat.SharedObject;
                return true;
            default:
                format = ArtifactFormat.SingleFileExe;
                return false;
        }
    }

    /// <summary>The wire name for a format, as the request accepts it and the
    /// library shows.</summary>
    public static string Name(ArtifactFormat format) => format switch
    {
        ArtifactFormat.SingleFileExe => "exe",
        ArtifactFormat.TrimmedExe => "exe-trimmed",
        ArtifactFormat.NativeAot => "aot",
        ArtifactFormat.Dll => "dll",
        ArtifactFormat.Shellcode => "shellcode",
        ArtifactFormat.SharedObject => "so",
        _ => format.ToString(),
    };
}
