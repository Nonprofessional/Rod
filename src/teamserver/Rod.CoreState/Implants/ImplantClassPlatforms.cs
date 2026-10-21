namespace Rod.CoreState.Implants;

/// <summary>
/// The platform surface each implant class covers (architecture.md Sec 5.2):
/// the axis the coverage matrix renders technique reach against
/// (architecture.md Sec 15.3, ADR-007). The process-hosting classes -- the
/// cross-platform implant, the TTL'd ephemeral, the forwarding pivot, and the
/// web-root shell -- run wherever the build matrix builds, linux and windows
/// today; the hooked browser runs in the browser, not on an OS the platform
/// builds for.
/// </summary>
/// <remarks>
/// This is display metadata for the coverage faces, not a gate: tasking reads
/// <see cref="ImplantClassCapabilities"/>; nothing in the task path consults
/// a platform. When the build matrix grows a target (an Apple or BSD triple),
/// this table follows it.
/// </remarks>
public static class ImplantClassPlatforms
{
    private static readonly IReadOnlyDictionary<ImplantClass, IReadOnlyList<string>> ByClass =
        new Dictionary<ImplantClass, IReadOnlyList<string>>
        {
            [ImplantClass.Implant] = ["linux", "windows"],
            [ImplantClass.Ephemeral] = ["linux", "windows"],
            [ImplantClass.Pivot] = ["linux", "windows"],
            [ImplantClass.WebShell] = ["linux", "windows"],
            [ImplantClass.Browser] = ["browser"],
        };

    /// <summary>
    /// The platforms <paramref name="class"/> covers. The ungated verbs
    /// (<see cref="ImplantClassCapabilities.Ungated"/>) belong to no class;
    /// callers render their reach as per-deployment rather than asking here.
    /// </summary>
    public static IReadOnlyList<string> For(ImplantClass @class)
        => ByClass.TryGetValue(@class, out var platforms) ? platforms : [];
}
