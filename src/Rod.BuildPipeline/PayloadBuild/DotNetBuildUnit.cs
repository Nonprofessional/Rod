using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Rod.CoreState.Implants;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The real .NET build unit (roadmap M3.3). Drives the reference .NET implant's
/// toolchain to compile a self-contained, per-implant artifact through the build
/// contract (architecture.md Sec 6). It runs <c>dotnet publish</c> against the
/// implant source tree, baking the per-implant profile into a generated
/// <c>BakedProfile.g.cs</c> source file so each artifact carries its own endpoint,
/// beacon parameters, and kill date (architecture.md Sec 5.1) -- and so the
/// per-implant key never has to be present at build time. Only the key's
/// fingerprint is recorded in the baked profile, never the key itself
/// (architecture.md Sec 7).
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
    // the repo root and lands at <root>/implant-dotnet (the tree added with M3.3).
    private readonly string _implantSourceDir;
    private readonly string _dotnetBinary;

    public Language Language => Language.DotNet;

    /// <summary>
    /// Builds a .NET build unit. <paramref name="implantSourceDir"/> is the implant
    /// tree to compile (the one containing the .csproj and Program.cs); defaults
    /// to <c>&lt;repo&gt;/implant-dotnet</c>. <paramref name="dotnetBinary"/> is the
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

        var now = DateTimeOffset.UtcNow;
        var baked = RenderBakedProfile(@params);

        // A unique temp work dir per build that mirrors the implant's relative
        // layout in the repo: <work>/implant-dotnet references <work>/src/Rod.
        // Protocol/protos/rod.proto via a relative path, and resolves CPM and the
        // shared build props from <work>. Copying that structure keeps the build
        // hermetic -- the real source tree is never mutated and two concurrent
        // builds never step on each other (also why the Go build unit partitions
        // GOCACHE).
        var workDir = Path.Combine(Path.GetTempPath(), "rod-dotnet-build-" + Guid.NewGuid().ToString("N"));
        var stagingDir = Path.Combine(workDir, "implant-dotnet");
        var outputDir = Path.Combine(workDir, "out");
        try
        {
            CopyTree(_implantSourceDir, stagingDir);

            // Reproduce the relative layout the csproj assumes: the proto is at
            // <work>/src/Rod.Protocol/protos/rod.proto (referenced as ../src/...
            // from the implant dir), and CPM + shared props sit at <work>/.
            CopyProtoTree(_implantSourceDir, workDir);
            CopyRepoProps(_implantSourceDir, workDir);

            // Overwrite the checked-in BakedProfile stub with the per-build profile.
            // The committed stub compiles empty so the implant runs from flags/env
            // during development; the build unit replaces it with the real profile
            // here, in the copy, leaving the source of truth untouched.
            var bakedPath = Path.Combine(stagingDir, "BakedProfile.cs");
            await File.WriteAllTextAsync(bakedPath, RenderBakedSource(baked), cancellationToken);

            // dotnet publish compiles the implant (including its proto client
            // bindings) and emits a framework-dependent assembly the operator's
            // build target can run via `dotnet Rod.Implant.dll`.
            var result = await RunDotNetAsync(
                new[]
                {
                    "publish",
                    "-c", "Release",
                    "-o", outputDir,
                    "-p:PublishSingleFile=false",
                    "--self-contained", "false",
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

            // The published Rod.Implant.dll is the artifact: the compiled implant
            // assembly plus its bundled dependencies. The whole output dir is what
            // a stager fetches; the dll is the entrypoint a `dotnet` invocation
            // loads. Returning the dll bytes keeps the fingerprint stable and the
            // artifact a single, recognizable blob.
            var dllPath = Path.Combine(outputDir, "Rod.Implant.dll");
            if (!File.Exists(dllPath))
                throw new InvalidOperationException(
                    "dotnet publish reported success but produced no Rod.Implant.dll.");

            var content = await File.ReadAllBytesAsync(dllPath, cancellationToken);
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
    // padding so it is safe to embed verbatim in a C# string literal. Identical to
    // GoBuildUnit.RenderBakedProfile: same keys, same encoding, so the two units'
    // baked profiles are interchangeable and the per-implant key never leaks --
    // only its fingerprint is recorded (architecture.md Sec 7). The implant reads
    // its key from the teamserver at enroll time, not from the baked profile.
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

    // Copies the repo-root MSBuild props (Directory.Build.props and
    // Directory.Packages.props) into the work dir so the staging copy of the
    // implant resolves CPM and the shared build settings the same way it does in
    // the real tree. The implant's csproj walks up from its dir to find these, so
    // they must sit one level above the staging copy. If a props file is absent
    // (e.g. an older tree), it is skipped rather than failing.
    private static void CopyRepoProps(string implantSourceDir, string workDir)
    {
        // The implant tree is <repo>/implant-dotnet, so the repo root is its parent.
        var repoRoot = Directory.GetParent(implantSourceDir)?.FullName;
        if (repoRoot is null || !Directory.Exists(repoRoot))
            return;
        foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props" })
        {
            var src = Path.Combine(repoRoot, name);
            if (File.Exists(src))
                File.Copy(src, Path.Combine(workDir, name), overwrite: true);
        }
    }

    // Copies the teamserver proto tree (src/Rod.Protocol/protos/) into the work
    // dir under the same relative path the implant's csproj references it by
    // (../src/Rod.Protocol/protos/ from the implant dir). The proto is the
    // single source of truth for the wire contract and is referenced, not
    // copied, by the csproj -- so the build needs the same relative layout to
    // find it. rod.proto has no imports, so only it (and its protos/ dir) is
    // needed.
    private static void CopyProtoTree(string implantSourceDir, string workDir)
    {
        var repoRoot = Directory.GetParent(implantSourceDir)?.FullName;
        if (repoRoot is null || !Directory.Exists(repoRoot))
            return;
        var protoSrcDir = Path.Combine(repoRoot, "src", "Rod.Protocol", "protos");
        if (!Directory.Exists(protoSrcDir))
            return;
        var protoDstDir = Path.Combine(workDir, "src", "Rod.Protocol", "protos");
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

    // The default implant tree is <repo>/implant-dotnet, found by walking up from
    // this assembly to the directory containing both 'src' and 'implant-dotnet'.
    // Keeps the build unit independent of the working directory it is invoked
    // from. Mirrors GoBuildUnit.ResolveDefaultImplantSourceDir.
    private static string ResolveDefaultImplantSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "implant-dotnet"))
                && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return Path.Combine(dir.FullName, "implant-dotnet");
            dir = dir.Parent;
        }
        // Fall back to a relative path so the error message in BuildAsync is clear.
        return Path.Combine("..", "..", "..", "..", "..", "implant-dotnet");
    }

    private static void TryCleanup(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort; temp dir is disposable */ }
    }

    // RFC 4648 base64url without padding -- matches the encoding used by the Go
    // build unit and the implant's own DecodeBase64Url decoder.
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
