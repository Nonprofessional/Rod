using System.Diagnostics;

namespace Rod.Integration.Tests;

/// <summary>
/// Shared test support. The in-tree .NET build unit and the .NET reference
/// implant end-to-end test drive the real dotnet toolchain and skip (not fail)
/// when dotnet is not on PATH, so the suite stays green in environments without
/// it while exercising the real slice where it is present.
/// </summary>
internal static class TestSupport
{
    /// <summary>
    /// True when the dotnet SDK is reachable on PATH. The in-tree .NET build/test
    /// path requires it to publish and run the reference .NET implant; tests that
    /// do skip via this check.
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

    // Builds a "<start>-<end>" port range for a recon.portscan argument that
    // covers a tight window around the given open port, so the scan finishes
    // promptly while still reporting the port as open. Clamped to [1, 65535].
    // (Relocated from the Go reference implant tests when that implant moved
    // out-of-tree, ADR 0009.)
    internal static string PortScanRangeAround(int port)
    {
        var start = Math.Max(1, port - 2);
        var end = Math.Min(65535, port + 2);
        return $"{start}-{end}";
    }
}
