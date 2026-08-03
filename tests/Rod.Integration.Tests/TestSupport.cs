using System.Diagnostics;

namespace Rod.Integration.Tests;

/// <summary>
/// Shared test support. The M3.2 Go build unit and the end-to-end Go implant
/// test drive a real <c>go</c> toolchain; both are skipped (not failed) when go
/// is not on PATH, so the suite stays green in environments without the Go
/// toolchain while exercising the real slice where it is present.
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
}
