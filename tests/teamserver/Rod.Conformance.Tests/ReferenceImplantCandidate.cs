using System.Diagnostics;

namespace Rod.Conformance.Tests;

/// <summary>
/// The reference Rust implant as a harness candidate: compiled once from the
/// source tree, then launched per phase as a real subprocess pointed at the
/// rig's endpoints through its dev shape (ROD_* environment). This is the
/// adapter that lets the harness's clause battery run against the implant an
/// operator actually deploys.
/// </summary>
public sealed class ReferenceImplantCandidate : IImplantCandidate
{
    public CandidateTransport Transport => CandidateTransport.Envelope;

    private readonly string _binary;
    private Process? _process;

    private ReferenceImplantCandidate(string binary)
    {
        _binary = binary;
    }

    /// <summary>
    /// Builds the reference implant from the source tree. Throws when the
    /// build fails so the failure is attributable.
    /// </summary>
    public static ReferenceImplantCandidate Build()
    {
        var tree = LocateRustTree()
            ?? throw new InvalidOperationException(
                "The Rust implant tree (src/implant/rust) was not found from the test assembly.");
        var psi = new ProcessStartInfo
        {
            FileName = "cargo",
            WorkingDirectory = tree,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add("--release");
        using var build = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start cargo (is it on PATH?).");
        var stdout = build.StandardOutput.ReadToEnd();
        var stderr = build.StandardError.ReadToEnd();
        build.WaitForExit(300_000);
        if (build.ExitCode != 0)
            throw new InvalidOperationException(
                $"cargo build failed (exit {build.ExitCode}):{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        var binary = Path.Combine(tree, "target", "release", "rod-implant");
        if (!File.Exists(binary))
            throw new InvalidOperationException($"cargo reported success but {binary} is missing.");
        return new ReferenceImplantCandidate(binary);
    }

    public Task StartAsync(ConformanceTarget target)
    {
        if (_process is { HasExited: false })
            throw new InvalidOperationException("The candidate is already running.");

        var psi = new ProcessStartInfo
        {
            FileName = _binary,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // The dev shape: the environment names the endpoints and the
        // credential, the poll carriage contacts the front that answered the
        // enrollment, and the plaintext lab posture keeps the harness's
        // frame reads simple.
        psi.Environment["ROD_ENROLL_URL"] = target.EnrollUrl;
        psi.Environment["ROD_STAGER_TOKEN"] = target.StagerToken;
        psi.Environment["ROD_SLEEP"] = "1";
        psi.Environment["ROD_JITTER"] = "0";
        psi.Environment["ROD_MODE"] = "poll";
        psi.Environment["ROD_ENVELOPE"] = "none";
        psi.Environment["ROD_VERBS"] = "shell.exec,file.pull,fs.list,beacon.sleep,proc.kill";
        if (target.KillDate is { } killDate)
            psi.Environment["ROD_KILL_DATE"] = killDate.ToString("O");
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

    public bool HasExited => _process is null || _process.HasExited;

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    // Walks up from the test assembly to the repo root, the same resolution
    // the in-tree build unit uses.
    private static string? LocateRustTree()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "implant", "rust"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "teamserver")))
                return Path.Combine(dir.FullName, "src", "implant", "rust");
            dir = dir.Parent!;
        }
        return null;
    }
}
