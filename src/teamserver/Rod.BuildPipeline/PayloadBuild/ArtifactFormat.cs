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
    /// The self-contained single-file native executable: runtime bundled and
    /// compressed, one file to drop and run, no target-side install. The
    /// default and the broadest-compatibility shape.
    /// </summary>
    SingleFileExe = 0,

    /// <summary>
    /// The single-file executable with IL trimming applied: the same
    /// drop-and-run deployment shape, materially smaller, at the cost of a
    /// trim-annotation-clean build (the reflection serializer was replaced with
    /// source generation to earn this).
    /// </summary>
    TrimmedExe = 1,

    /// <summary>
    /// Native AOT compilation: a fully ahead-of-time-compiled native binary
    /// with no runtime to bundle or bootstrap -- the smallest and
    /// fastest-starting executable shape, with the same no-runtime deployment
    /// property a C or Go artifact has. Incompatible with in-process assembly
    /// loading by construction.
    /// </summary>
    NativeAot = 2,

    /// <summary>
    /// The framework-dependent managed DLL bundle: a zip of the publish output
    /// (entry assembly, dependency assemblies, deps/runtimeconfig), sized for a
    /// single fetch and load. A host with a compatible .NET runtime loads it
    /// in-process via <c>Assembly.Load</c> with no bytes on disk -- the
    /// stager's host shape and the pwsh cradle's payload.
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
