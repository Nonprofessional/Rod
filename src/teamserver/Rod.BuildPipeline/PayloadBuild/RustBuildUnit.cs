using System.Diagnostics;
using Rod.CoreState.Implants;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The Rust build unit: drives cargo against the Rust reference implant tree
/// through the same build contract the .NET unit serves (architecture.md
/// Sec 6, Sec 12.2). The unit copies the tree into a per-build staging dir,
/// overwrites the checked-in baked-profile stub with the per-artifact profile
/// (the same base64url JSON the .NET unit bakes, the language-neutral wire
/// contract), and compiles a release binary for the requested target
/// triple. Rust is always native code: the executable formats are synonyms
/// here (the size posture lives in the crate's release profile -- opt-level,
/// LTO, strip; 'aot' differs only as the spelling the memfd launcher family
/// keys on), and the dll format is refused outright.
/// </summary>
/// <remarks>
/// The transport and handler trims the .NET unit applies have Rust
/// equivalents ahead: transport selection and verb trimming will ride cargo
/// features (compile only the contact clients and handlers the bake dials),
/// and the Windows-sensitive verbs already self-gate on <c>cfg(windows)</c>
/// so a Linux build compiles none of them.
///
/// Build failures throw <see cref="BuildUnitFailureException"/> (a
/// server-side fault: the toolchain, the source tree, or the host is wrong)
/// while contract refusals -- retired classes and formats, unsupported
/// target triples -- throw <see cref="InvalidOperationException"/> (the
/// request is wrong). Set <c>ROD_RUST_TARGET_DIR</c> to point builds at a
/// shared, persistent cargo target dir and reuse dependency compilation
/// across builds.
/// </remarks>
public sealed class RustBuildUnit : IBuildUnit
{
    private readonly string _rustSourceDir;
    private readonly string _cargoBinary;
    private readonly string? _sharedTargetDir;

    public Language Language => Language.Rust;

    public RustBuildUnit(string? rustSourceDir = null, string? cargoBinary = null)
    {
        _rustSourceDir = string.IsNullOrWhiteSpace(rustSourceDir)
            ? ResolveDefaultRustSourceDir()
            : rustSourceDir;
        _cargoBinary = cargoBinary ?? "cargo";
        // The shared-target opt-in rides the environment rather than the
        // caller's configuration: the unit is composed once in the transport
        // host, and a deployment (or a CI test lane) should not have to
        // re-compose it to point builds at a warm compile cache.
        _sharedTargetDir = NonEmptyOrNull(Environment.GetEnvironmentVariable("ROD_RUST_TARGET_DIR"));
    }

    private static string? NonEmptyOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    public async Task<BuildArtifact> BuildAsync(BuildParams @params, CancellationToken cancellationToken = default)
    {
        if (@params.Class == ImplantClass.Stager)
            throw new InvalidOperationException(
                "The stager class is retired with the .NET trees; deliver the implant through the launcher one-liners.");
        if (@params.Format == ArtifactFormat.Dll)
            throw new InvalidOperationException(
                "The dll format is retired with the .NET implant; every Rust artifact is a native executable -- use 'exe' or 'aot'.");
        if (!Directory.Exists(_rustSourceDir))
            throw new BuildUnitFailureException($"Rust implant source tree not found at '{_rustSourceDir}'.");

        // The bake: the same base64url profile JSON every unit emits --
        // one language-neutral contract, decoded identically by every
        // implant.
        var baked = ProfileBake.Render(@params);
        var triple = MapTriple(@params.Target);

        var workDir = Path.Combine(Path.GetTempPath(), "rod-rust-build-" + Guid.NewGuid().ToString("N"));
        // The staging copy mirrors the crate's real repo layout: build.rs
        // references the teamserver proto at ../../teamserver/... relative to
        // the crate dir, so the copy sits at <work>/src/implant/rust with the
        // proto at <work>/src/teamserver/Rod.Protocol/protos -- the same
        // relative dance the .NET unit performs for its csproj's proto
        // reference.
        var stagingDir = Path.Combine(workDir, "src", "implant", "rust");
        // Hermetic by default: a per-build target dir under the disposable
        // work dir. ROD_RUST_TARGET_DIR opts a deployment into a shared,
        // persistent dir instead: every registry dependency's compiled
        // artifacts are reused across builds (only the implant crate itself
        // recompiles, since its staging path is per-build), which turns a
        // cold full cross-compile into a one-time cost. Concurrent builds
        // through a shared dir queue on cargo's own target-dir lock -- they
        // wait, not fail.
        var targetDir = _sharedTargetDir ?? Path.Combine(workDir, "target");
        try
        {
            CopyTree(_rustSourceDir, stagingDir);
            CopyProtoTree(_rustSourceDir, workDir);

            // Overwrite the checked-in baked.rs stub with the per-build
            // profile, the same mechanism the .NET trees' BakedProfile uses.
            var bakedPath = Path.Combine(stagingDir, "src", "baked.rs");
            await File.WriteAllTextAsync(
                bakedPath,
                "// <auto-generated> Generated by Rod.RustBuildUnit at build time.\n"
                    + "// The per-artifact profile, baked in at generation (architecture.md Sec 5.1),\n"
                    + "// base64url JSON with the same keys every build unit emits.\n"
                    + "pub const PROFILE: &str = \"" + baked + "\";\n",
                cancellationToken);

            // A per-build target dir keeps builds hermetic (the shared cargo
            // home and registry cache stay warm across builds).
            var arguments = new List<string> { "build", "--release", "--target", triple };
            var cargo = new ProcessStartInfo
            {
                FileName = _cargoBinary,
                WorkingDirectory = stagingDir,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var argument in arguments)
                cargo.ArgumentList.Add(argument);
            cargo.Environment["CARGO_TARGET_DIR"] = targetDir;
            cargo.Environment["CARGO_NET_GIT_FETCH_WITH_CLI"] = "true";
            // The static (musl) triples: rust ships their std, and the C
            // bits -- ring's primitives -- need a musl cross compiler the
            // build host names per target. A host without the cross fails
            // inside cargo with the toolchain's own error, loudly.
            if (triple.EndsWith("-musl", StringComparison.Ordinal))
            {
                var envStem = triple.Replace('-', '_').ToUpperInvariant();
                var prefix = triple.Split('-')[0] switch
                {
                    "x86_64" => "musl-gcc",
                    "aarch64" => "aarch64-linux-musl-gcc",
                    "armv7" => "armv7-linux-musleabihf-gcc",
                    _ => "musl-gcc",
                };
                cargo.Environment[$"CC_{envStem}"] = prefix;
                cargo.Environment[$"AR_{envStem}"] = prefix.Replace("gcc", "ar");
            }
            var result = await RunAsync(cargo, cancellationToken);
            if (result.ExitCode != 0)
            {
                var diag = result.Stdout;
                if (result.Stderr.Length > 0)
                    diag = (diag.Length > 0 ? diag + "\n" : "") + result.Stderr;
                throw new BuildUnitFailureException(
                    $"cargo build failed (exit {result.ExitCode}):\n{diag}");
            }

            var binaryName = triple.StartsWith("windows", StringComparison.Ordinal)
                ? "rod-implant.exe"
                : "rod-implant";
            var binaryPath = Path.Combine(targetDir, triple, "release", binaryName);
            if (!File.Exists(binaryPath))
                throw new BuildUnitFailureException(
                    $"cargo reported success but produced no {binaryName} for {triple}.");

            var content = await File.ReadAllBytesAsync(binaryPath, cancellationToken);
            return BuildArtifact.Of(
                Language,
                artifactId: Guid.NewGuid(),
                @params,
                content,
                contentType: "application/octet-stream",
                builtAt: DateTimeOffset.UtcNow);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch { /* best-effort; temp dir is disposable */ }
        }
    }

    // Maps the contract's os/arch pairs onto Rust target triples. Linux is
    // musl everywhere -- the static, runtime-free deployment shape that is
    // the reach implant's whole point (routers, appliances, IoT); measured
    // x86_64: 2.25 MB fully static. Apple targets need the Apple SDK a
    // Linux build host does not carry, and ARM Windows has no GNU target, so
    // both refuse with the supported set named rather than failing inside
    // cargo with a less fixable error. The ARM musl triples map too; they
    // build once the host carries their cross toolchains.
    public static string MapTriple(TargetProfile target)
    {
        var os = target.OperatingSystem.Trim().ToLowerInvariant();
        var arch = target.Architecture.Trim().ToLowerInvariant();
        return (os, arch) switch
        {
            ("linux", "amd64" or "x64" or "x86_64") => "x86_64-unknown-linux-musl",
            ("linux", "arm64" or "aarch64") => "aarch64-unknown-linux-musl",
            ("linux", "arm" or "armv7") => "armv7-unknown-linux-musleabihf",
            ("linux", "x86" or "386") => "i686-unknown-linux-musl",
            ("windows" or "win", "amd64" or "x64" or "x86_64") => "x86_64-pc-windows-gnu",
            ("windows" or "win", "x86" or "386") => "i686-pc-windows-gnu",
            ("osx" or "darwin" or "macos", _) => throw new InvalidOperationException(
                "The Rust build unit does not cross Apple targets (the link needs the Apple SDK); build on a macOS host."),
            ("windows" or "win", "arm64" or "aarch64") => throw new InvalidOperationException(
                "The Rust build unit has no GNU target for ARM Windows."),
            _ => throw new InvalidOperationException(
                $"Unsupported target '{target.OperatingSystem}/{target.Architecture}' for the Rust build unit " +
                "(supported: linux amd64/arm64/arm/x86, windows amd64/x86)."),
        };
    }

    // Recursively copies the crate, skipping target/ (cargo's build output)
    // so a stale build never leaks in. Cargo.lock rides along: the lock pins
    // the dependency set, keeping per-build compiles reproducible.
    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (name is "target")
                continue;
            var to = Path.Combine(destination, name);
            if (Directory.Exists(entry))
                CopyTree(entry, to);
            else
                File.Copy(entry, to, overwrite: true);
        }
    }

    // Copies the teamserver proto tree into the work dir under the same
    // relative path build.rs references it by (../../teamserver/... from the
    // crate dir). The proto is the wire contract's single source of truth;
    // rod.proto has no imports, so the directory holds all it needs.
    private static void CopyProtoTree(string rustSourceDir, string workDir)
    {
        var dir = new DirectoryInfo(rustSourceDir);
        while (dir is not null)
        {
            var protoDir = Path.Combine(dir.FullName, "src", "teamserver", "Rod.Protocol", "protos");
            if (Directory.Exists(protoDir))
            {
                var to = Path.Combine(workDir, "src", "teamserver", "Rod.Protocol", "protos");
                Directory.CreateDirectory(to);
                foreach (var file in Directory.EnumerateFiles(protoDir))
                    File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
                return;
            }
            dir = dir.Parent!;
        }
        throw new BuildUnitFailureException(
            "The Rust build needs the teamserver proto tree (src/teamserver/Rod.Protocol/protos) beside the crate; the repo walk-up found none.");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new BuildUnitFailureException($"Failed to start cargo ('{start.FileName}').");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string ResolveDefaultRustSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "implant", "rust"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "teamserver")))
                return Path.Combine(dir.FullName, "src", "implant", "rust");
            dir = dir.Parent;
        }
        return Path.Combine("..", "..", "..", "..", "..", "src", "implant", "rust");
    }
}
