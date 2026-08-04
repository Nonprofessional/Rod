using System.Diagnostics;

namespace Rod.Build.Tests;

/// <summary>
/// Shared test support for the build-unit tests. The build units drive real
/// toolchains (go, dotnet); tests that compile a real implant skip (not fail)
/// when the toolchain is not on PATH. Mirrors the helper in
/// Rod.Integration.Tests.
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

    // The .NET build unit drives the dotnet SDK to publish the reference implant;
    // tests that compile a real implant skip (not fail) when dotnet is not on
    // PATH. dotnet is effectively always present in a .NET repo, but the check
    // keeps the suite green in a stripped-down environment the same way the Go
    // check does.
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
