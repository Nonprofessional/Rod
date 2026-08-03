namespace Rod.Tradecraft.Capabilities;

/// <summary>
/// How a dispatched capability turned out (architecture.md Sec 10.3). Mirrors
/// the core-state <c>TaskOutcome</c> split but adds <see cref="NotFound"/>: a
/// dispatch against an unregistered verb is a normal, non-throwing result so the
/// caller (a future task-issuance gate, or a test) can treat "no module handles
/// this" as a value rather than an exception.
/// </summary>
public enum CapabilityStatus
{
    /// <summary>The verb ran and reported success.</summary>
    Succeeded,

    /// <summary>The verb ran but reported failure (e.g. non-zero exit).</summary>
    Failed,

    /// <summary>No module is registered for the requested verb.</summary>
    NotFound,
}

/// <summary>
/// The outcome of dispatching a <see cref="CapabilityInvocation"/>: the status,
/// the captured <see cref="Output"/> (stdout/stderr equivalent, empty when none),
/// and a free-form <see cref="Error"/> message on failure. The dispatcher builds
/// <see cref="NotFound"/> itself; a module builds <see cref="Succeeded"/> or
/// <see cref="Failed"/> from its execution.
/// </summary>
public sealed record CapabilityResult(
    CapabilityStatus Status,
    string Output,
    string? Error)
{
    /// <summary>A successful run with the given <paramref name="output"/>.</summary>
    public static CapabilityResult Succeeded(string output)
        => new(CapabilityStatus.Succeeded, output, Error: null);

    /// <summary>A failed run; <paramref name="error"/> carries the reason.</summary>
    public static CapabilityResult Failed(string error)
        => new(CapabilityStatus.Failed, Output: string.Empty, error);

    /// <summary>No module is registered for the requested verb.</summary>
    public static CapabilityResult NotFoundFor(string verb)
        => new(CapabilityStatus.NotFound, Output: string.Empty, Error: $"No capability module is registered for '{verb}'.");
}
