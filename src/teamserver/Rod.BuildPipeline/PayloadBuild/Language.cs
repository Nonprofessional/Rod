namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The implant language a build unit compiles for (architecture.md Sec 6,
/// Sec 12). One build unit per language; the registry routes a build request to
/// its unit by this value. The teamserver stays language-agnostic at the build
/// boundary -- it sends <see cref="BuildParams"/> and gets a
/// <see cref="BuildArtifact"/> back, coupled to the unit only by this contract.
/// </summary>
public enum Language
{
    /// <summary>Go -- cross-platform implants and the redirector language.</summary>
    Go,

    /// <summary>C#/.NET -- Windows in-memory tradecraft.</summary>
    DotNet,

    /// <summary>C/C++ -- small footprint implants.</summary>
    C,

    /// <summary>Nim -- small footprint implants.</summary>
    Nim,
}
