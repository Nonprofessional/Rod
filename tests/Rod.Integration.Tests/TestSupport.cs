using System.Diagnostics;

namespace Rod.Integration.Tests;

/// <summary>
/// Shared test support. The M3.2 Go build unit / end-to-end test and the M3.3
/// .NET build unit / end-to-end test drive real toolchains (go, dotnet); both
/// skip (not fail) when the toolchain is not on PATH, so the suite stays green in
/// environments without them while exercising the real slice where they are
/// present.
/// </summary>
internal static class TestSupport
{
    /// <summary>
    /// True when the go toolchain is reachable on PATH. The M3.2 build/test path
    /// requires it; tests that compile a real implant skip via this check.
    /// </summary>
    public static bool GoAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("go", "version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the dotnet SDK is reachable on PATH. The M3.3 build/test path
    /// requires it to publish and run the reference .NET implant; tests that do
    /// skip via this check.
    /// </summary>
    public static bool DotNetAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.WaitForExit(15000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
