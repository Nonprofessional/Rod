using System.Diagnostics;

namespace Rod.Conformance.Tests;

/// <summary>
/// The reference .NET implant as a harness candidate: published once into a
/// temp dir, then launched per phase as a real subprocess pointed at the
/// rig's endpoints. This is the adapter that lets the harness's clause
/// battery run against the implant an operator actually deploys.
/// </summary>
public sealed class ReferenceImplantCandidate : IImplantCandidate
{
    public CandidateTransport Transport => CandidateTransport.GRpc;

    private readonly string _implantDll;
    private readonly string? _publishDir;
    private Process? _process;

    private ReferenceImplantCandidate(string implantDll, string publishDir)
    {
        _implantDll = implantDll;
        _publishDir = publishDir;
    }

    /// <summary>
    /// Publishes the reference implant from the source tree into a temp dir.
    /// Throws when the publish fails so the failure is attributable.
    /// </summary>
    public static ReferenceImplantCandidate Publish()
    {
        var source = LocateImplantSource();
        var outDir = Path.Combine(Path.GetTempPath(), "rod-conformance-implant-" + Guid.NewGuid().ToString("N"));
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = source,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("publish");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("/clp:NoSummary");
        using var publish = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet publish.");
        var stdout = publish.StandardOutput.ReadToEnd();
        var stderr = publish.StandardError.ReadToEnd();
        publish.WaitForExit();
        if (publish.ExitCode != 0)
            throw new InvalidOperationException(
                $"dotnet publish failed (exit {publish.ExitCode}):{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        return new ReferenceImplantCandidate(Path.Combine(outDir, "Rod.Implant.dll"), outDir);
    }

    public bool HasExited => _process is null || _process.HasExited;

    public Task StartAsync(ConformanceTarget target)
    {
        if (_process is { HasExited: false })
            throw new InvalidOperationException("The candidate is already running.");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(_implantDll);
        psi.ArgumentList.Add("-enroll-url");
        psi.ArgumentList.Add(target.EnrollUrl);
        psi.ArgumentList.Add("-beacon-url");
        psi.ArgumentList.Add(target.BeaconHostPort);
        psi.ArgumentList.Add("-token");
        psi.ArgumentList.Add(target.StagerToken);
        psi.ArgumentList.Add("-ca-cert");
        psi.ArgumentList.Add(target.CaPemPath);
        psi.ArgumentList.Add("-sleep");
        psi.ArgumentList.Add("1s");
        psi.ArgumentList.Add("-jitter");
        psi.ArgumentList.Add("0s");
        if (target.KillDate is { } killDate)
        {
            psi.ArgumentList.Add("-kill-date");
            psi.ArgumentList.Add(killDate.ToString("O"));
        }
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the implant.");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_process is null)
            return;
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            await _process.WaitForExitAsync(CancellationToken.None);
        }
        _process.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        _process?.Dispose();
        try { if (_publishDir is not null) Directory.Delete(_publishDir, recursive: true); } catch { }
    }

    // Walks up from the test assembly to the repo root, the same resolution
    // the in-tree build unit uses.
    private static string LocateImplantSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "implant", "dotnet"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "teamserver")))
                return Path.Combine(dir.FullName, "src", "implant", "dotnet");
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the .NET implant source tree from the test assembly.");
    }
}
