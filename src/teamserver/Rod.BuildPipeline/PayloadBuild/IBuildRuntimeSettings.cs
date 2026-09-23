namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The build pipeline's live knobs (the settings page's build section): the
/// values an operator adjusts while the teamserver runs, read by the build
/// units on every build so a change applies to the next job without a
/// restart. A port, not a configuration binding: the transport layer owns
/// the live holder and its persistence, and the units depend only on this
/// seam (the same layering the sweeper's settings follow).
/// </summary>
public interface IBuildRuntimeSettings
{
    /// <summary>
    /// The cargo target directory shared across builds -- the warm compile
    /// cache. Null keeps builds hermetic (a fresh target dir per build, so
    /// every cold cross-compile pays the full dependency build).
    /// </summary>
    string? RustTargetDir { get; }
}
