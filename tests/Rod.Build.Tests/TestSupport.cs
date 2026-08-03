using System.Diagnostics;

namespace Rod.Build.Tests;

/// <summary>
/// Shared test support for the build-unit tests. The Go build unit drives a real
/// go toolchain; tests that compile a real implant skip (not fail) when go is not
/// on PATH. Mirrors the helper in Rod.Integration.Tests.
/// </summary>
internal static class TestSupport
{
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
}
