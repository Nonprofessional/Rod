using System.Runtime.InteropServices;

namespace Rod.Implant.Internal;

/// <summary>
/// The facts the implant reports about the machine it runs on, sent once at
/// enroll: machine name, OS description, CPU architecture, and the account the
/// process runs under. The teamserver records them on the implant as its device
/// identity, so operators see which host a beacon lives on and the fleet can
/// group implants by device. Best-effort throughout: a fact the runtime cannot
/// name is null, and the enroll request simply omits it.
/// </summary>
/// <remarks>
/// Lives in its own always-compiled file, apart from any handler source: the
/// program and the lateral.move child-enroll path both consume it, so it must
/// survive the bake-time handler trim no matter which verbs a reduced class
/// keeps.
/// </remarks>
internal sealed record HostIdentity(string? Hostname, string? Os, string? Arch, string? Username)
{
    /// <summary>
    /// Captures the current machine's facts. Each value is trimmed and emptied
    /// to null when the runtime has nothing to say, so a fact is either a real
    /// name or absent -- never whitespace.
    /// </summary>
    public static HostIdentity Capture() => new(
        Clean(Environment.MachineName),
        Clean(RuntimeInformation.OSDescription),
        Clean(RuntimeInformation.OSArchitecture.ToString()),
        Clean(Environment.UserName));

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
