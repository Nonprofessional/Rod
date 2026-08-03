namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// A per-language build unit (architecture.md Sec 6, Sec 12). One per implant
/// language (Go, C#/.NET, C/C++, Nim), each owning its own toolchain. The
/// teamserver drives them through this uniform contract and is coupled to them
/// only by it -- a .NET teamserver produces a Go or C implant with no in-language
/// coupling. <see cref="Language"/> routes a build request to its unit via
/// <see cref="IBuildUnitRegistry"/>.
/// </summary>
public interface IBuildUnit
{
    /// <summary>The implant language this unit compiles for.</summary>
    Language Language { get; }

    /// <summary>
    /// Compiles <paramref name="params"/> into a fingerprinted artifact. Build
    /// params are produced at request time so each artifact is unique
    /// (architecture.md Sec 6); the unit bakes them in and returns the bytes.
    /// </summary>
    Task<BuildArtifact> BuildAsync(BuildParams @params, CancellationToken cancellationToken = default);
}
