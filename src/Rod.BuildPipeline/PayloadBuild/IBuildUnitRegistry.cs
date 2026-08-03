namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// Resolves a build unit by <see cref="Language"/> (architecture.md Sec 6). The
/// teamserver is coupled to build units only by the build contract, and there is
/// one unit per language, so a build request names its language and the registry
/// hands back the unit that owns that toolchain. The registry is process-local
/// state; build units register into it at startup.
/// </summary>
public interface IBuildUnitRegistry
{
    /// <summary>Registers <paramref name="unit"/> under its language.</summary>
    void Register(IBuildUnit unit);

    /// <summary>
    /// The build unit for <paramref name="language"/>, or null when no unit is
    /// registered for it.
    /// </summary>
    IBuildUnit? Find(Language language);
}
