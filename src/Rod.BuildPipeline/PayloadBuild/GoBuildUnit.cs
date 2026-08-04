using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rod.CoreState.Implants;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The real Go build unit (roadmap M3.2). Drives the reference Go implant's
/// toolchain to compile a self-contained, per-implant artifact through the build
/// contract (architecture.md Sec 6). It runs <c>go build</c> against the implant
/// source tree, injecting the baked profile via <c>-ldflags -X main.bakedJSON</c>
/// so each artifact carries its own endpoint, beacon parameters, and kill date
/// (architecture.md Sec 5.1) -- and so the per-implant key never has to be present
/// at build time. Only the key's fingerprint is recorded in the manifest the build
/// unit logs, never the key itself (architecture.md Sec 7).
///
/// The teamserver is coupled to this unit only by the build contract: it sends
/// <see cref="BuildParams"/> and gets a <see cref="BuildArtifact"/> back, and the
/// Go toolchain lives entirely on the build-unit side. The unit throws a clear
/// error when <c>go</c> is missing or the build fails, so the build endpoint maps
/// that to a 5xx rather than a silent stub.
/// </summary>
public sealed class GoBuildUnit : IBuildUnit
{
    // The implant source tree, relative to the build-pipeline project, that this
    // unit compiles. Overridable via the constructor so tests can point at a
    // fixture or skip a real build. The default walks up from the assembly to find
    // the repo root and lands at <root>/implant (the tree added with M3.2).
    private readonly string _implantSourceDir;
    private readonly string _goBinary;
    private readonly string? _goCacheRoot;

    public Language Language => Language.Go;

    /// <summary>
    /// Builds a Go build unit. <paramref name="implantSourceDir"/> is the implant
    /// tree to compile (the one containing go.mod and cmd/rod-implant); defaults
    /// to <c>&lt;repo&gt;/implant</c>. <paramref name="goBinary"/> is the go
    /// executable, defaulting to PATH resolution of <c>go</c>.
    /// <paramref name="goCacheRoot"/> overrides GOCACHE so concurrent builds do not
    /// race on the default shared cache; null uses the user's default GOCACHE.
    /// </summary>
    public GoBuildUnit(
        string? implantSourceDir = null,
        string? goBinary = null,
        string? goCacheRoot = null)
    {
        _implantSourceDir = implantSourceDir ?? ResolveDefaultImplantSourceDir();
        _goBinary = goBinary ?? "go";
        _goCacheRoot = goCacheRoot;
    }

    public async Task<BuildArtifact> BuildAsync(BuildParams @params, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_implantSourceDir))
            throw new InvalidOperationException(
                $"Go implant source tree not found at '{_implantSourceDir}'.");

        var now = DateTimeOffset.UtcNow;
        var baked = RenderBakedProfile(@params);

        // A unique temp output per build; GOCACHE is partitioned per build so two
        // concurrent builds never step on each other's compile cache. Both are
        // cleaned up when the scope leaves.
        var workDir = Path.Combine(Path.GetTempPath(), "rod-go-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var goCache = _goCacheRoot ?? Path.Combine(workDir, "gocache");
        Directory.CreateDirectory(goCache);
        var outputFile = Path.Combine(workDir, OutputName(@params.Target));

        try
        {
            // go build injects the baked profile as a string constant via ldflags;
            // GOOS/GOARCH select the cross-compile target from the build params.
            var ldflags = $"-X main.bakedJSON={baked}";
            var goos = NormalizeGoOs(@params.Target.OperatingSystem);
            var goarch = NormalizeGoArch(@params.Target.Architecture);

            var result = await RunGoAsync(
                new[] { "build", "-ldflags", ldflags, "-o", outputFile, "./cmd/rod-implant" },
                goos, goarch, goCache,
                cancellationToken);

            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"go build failed (exit {result.ExitCode}):\n{result.Stderr}");

            if (!File.Exists(outputFile))
                throw new InvalidOperationException(
                    "go build reported success but produced no output binary.");

            var content = await File.ReadAllBytesAsync(outputFile, cancellationToken);
            return BuildArtifact.Of(
                Language,
                artifactId: Guid.NewGuid(),
                @params,
                content,
                contentType: "application/octet-stream",
                builtAt: now);
        }
        finally
        {
            TryCleanup(workDir);
        }
    }

    // Renders the baked profile as a compact JSON map, base64-url-encoded without
    // padding so it is safe to pass verbatim through go's -X ldflags argument
    // (which has no shell layer -- ProcessStartInfo argument passing -- but a
    // URL-safe, padding-free encoding stays robust against any future wrapping).
    // The per-implant key is intentionally absent: only its fingerprint is
    // recorded, so the artifact cannot leak the key it was built with
    // (architecture.md Sec 7). The implant reads its key from the teamserver at
    // enroll time, not from the baked profile.
    public static string RenderBakedProfile(BuildParams @params)
    {
        var keyFingerprint = ArtifactFingerprint.Of(Encoding.UTF8.GetBytes(@params.Key));
        // The class's reduced verb set (architecture.md Sec 5.2), comma-joined so
        // the artifact is self-describing: the generated implant carries the verbs
        // it is permitted to run, baked in alongside its profile.
        var verbs = string.Join(",", ImplantClassCapabilities.For(@params.Class));
        var map = new Dictionary<string, string>
        {
            ["enrollURL"] = @params.Transport.Endpoint,
            ["beaconURL"] = BeaconUrlFromEnroll(@params.Transport.Endpoint),
            ["killDate"] = @params.Beacon.KillDate.ToString("O"),
            ["sleep"] = ((long)@params.Beacon.Sleep.TotalSeconds).ToString() + "s",
            ["jitter"] = ((long)@params.Beacon.Jitter.TotalSeconds).ToString() + "s",
            ["uriPath"] = @params.Transport.UriPath,
            ["verbs"] = verbs,
            ["keyFingerprint"] = keyFingerprint,
        };
        var json = JsonSerializer.Serialize(map);
        return Base64Url(Encoding.UTF8.GetBytes(json));
    }

    // The beacon URL is the enroll endpoint with /implants/enroll stripped. The
    // build params carry a single endpoint; the implant accepts an explicit beacon
    // URL when enroll and beacon hosts differ (a redirector in front).
    private static string BeaconUrlFromEnroll(string enrollEndpoint)
    {
        const string suffix = "/implants/enroll";
        if (enrollEndpoint.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return enrollEndpoint[..^suffix.Length];
        return enrollEndpoint;
    }

    // go expects lowercase goos/goarch (linux/amd64, windows/amd64). The build
    // params' Target is a free-form OS/arch string; normalize the common spellings
    // and pass anything else through verbatim -- go fails clearly on an unknown
    // target, which surfaces as a build error here.
    private static string NormalizeGoOs(string? os)
        => (os ?? "linux").Trim().ToLowerInvariant() switch
        {
            "linux" => "linux",
            "windows" or "win" => "windows",
            "darwin" or "macos" or "osx" => "darwin",
            var other => other,
        };

    private static string NormalizeGoArch(string? arch)
        => (arch ?? "amd64").Trim().ToLowerInvariant() switch
        {
            "amd64" or "x86_64" or "x64" => "amd64",
            "arm64" or "aarch64" => "arm64",
            "386" or "x86" or "i386" => "386",
            var other => other,
        };

    // The output filename, with the right extension for the target OS so a
    // Windows build is a .exe. Only the extension differs; the basename is stable.
    private static string OutputName(TargetProfile target)
        => NormalizeGoOs(target.OperatingSystem) == "windows"
            ? "rod-implant.exe"
            : "rod-implant";

    // Runs go with GOOS/GOARCH/GOCACHE set in its environment, from the implant
    // source directory. Captures stderr for the error message on failure.
    private Task<(int ExitCode, string Stderr)> RunGoAsync(
        string[] args,
        string goos, string goarch, string goCache,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _goBinary,
            WorkingDirectory = _implantSourceDir,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        // Cross-compile and isolate the cache from concurrent builds.
        psi.Environment["GOOS"] = goos;
        psi.Environment["GOARCH"] = goarch;
        psi.Environment["GOCACHE"] = goCache;
        // A reproducible build: trim the file-path prefix embedded in the binary
        // so two builds of the same source+profile agree, and drop the VCS stamp
        // (the implant tree is not a standalone git repo).
        psi.Environment["GOFLAGS"] = "-trimpath -mod=readonly";
        psi.Environment["CGO_ENABLED"] = "0";

        var process = new Process { StartInfo = psi };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start go ('{_goBinary}').");
        process.BeginErrorReadLine();
        // stdout is not needed; drain it asynchronously so a full pipe cannot
        // block the build. BeginOutputReadLine pumps to OutputDataReceived which
        // has no handler here, so the data is read and discarded.
        process.BeginOutputReadLine();

        var tcs = new TaskCompletionSource<(int, string)>();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            process.WaitForExit();
            tcs.TrySetResult((process.ExitCode, stderr.ToString()));
        };

        var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            tcs.TrySetCanceled(cancellationToken);
        });

        return tcs.Task.ContinueWith(t =>
        {
            registration.Dispose();
            process.Dispose();
            return t.Result;
        }, cancellationToken);
    }

    // The default implant tree is <repo>/implant, found by walking up from this
    // assembly to the directory containing both 'src' and 'implant'. Keeps the
    // build unit independent of the working directory it is invoked from.
    private static string ResolveDefaultImplantSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "implant"))
                && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return Path.Combine(dir.FullName, "implant");
            dir = dir.Parent;
        }
        // Fall back to a relative path so the error message in BuildAsync is clear.
        return Path.Combine("..", "..", "..", "..", "..", "implant");
    }

    private static void TryCleanup(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort; temp dir is disposable */ }
    }

    // RFC 4648 base64url without padding -- matches the encoding used elsewhere
    // in the build pipeline and the implant's own seedFromBaked decoder.
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
