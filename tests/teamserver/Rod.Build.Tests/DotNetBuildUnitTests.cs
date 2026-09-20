using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.Build.Tests;

/// <summary>
/// Unit tests for the real .NET build unit: the contract's .NET
/// producer. Proves the unit compiles a non-empty, fingerprinted artifact for the
/// configured target, that two builds of the same params never share a
/// fingerprint (per-implant material is generated at request time), and that the
/// baked profile does not leak the per-implant key into the artifact. The
/// per-build tests are skipped when dotnet is not on PATH.
/// </summary>
public class DotNetBuildUnitTests
{
    private static BuildParams Params(ImplantClass @class = ImplantClass.Stage2) => new(
        EngagementId.New(),
        OperatorId.New(),
        @class,
        new TargetProfile("linux", "amd64"),
        new TransportProfile("http://c2.example.test/implants/enroll", "/beacon"),
        new BeaconProfile(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddDays(30)));

    [Theory]
    [InlineData(ImplantClass.Stage2, "shell.exec,shell.interact,file.push,file.pull,fs.list,proc.kill,beacon.sleep,tunnel.forward,tunnel.socks,recon.portscan,recon.hostenum,recon.service,recon.ps,lateral.move,lateral.token,lateral.exec_remote,persist.install,persist.remove,persist.list,collect.cred,collect.keylog,collect.screenshot,exfil.push,exfil.stage,evasion.avoid,evasion.unload,exploit.invoke,exploit.module")]
    [InlineData(ImplantClass.Stager, "file.pull,evasion.avoid,evasion.unload,exploit.invoke,exploit.module")]
    [InlineData(ImplantClass.Pivot, "tunnel.forward,tunnel.socks,evasion.avoid,evasion.unload,exploit.invoke,exploit.module")]
    public void RenderBakedProfile_BakesClassVerbsPlusTheUngatedContractVerbs(ImplantClass @class, string expectedVerbs)
    {
        // The class's reduced verb set (architecture.md Sec 5.2) plus the
        // contract-only verbs no class gates (Sec 5.2/10.2) is baked into the
        // profile, so the generated implant carries the verbs it may run and an
        // out-of-tree evasion/exploit handler can advertise its verb. The
        // advertised set is still intersected with the compiled handlers, so the
        // extra verbs claim nothing on an artifact without the handler.
        var baked = DotNetBuildUnit.RenderBakedProfile(Params(@class));

        var json = Encoding.UTF8.GetString(Base64UrlDecode(baked));

        Assert.Contains($"\"verbs\":\"{expectedVerbs}\"", json);
    }

    [Fact]
    public void RenderBakedProfile_BakesTheConfiguredBeaconProfile()
    {
        // The configured beacon profile (architecture.md Sec 5.1, Sec 7) -- sleep,
        // jitter, kill date -- is what makes per-implant OPSEC possible, so it must
        // land in the decoded artifact, not be silently dropped. Use values that
        // differ from the build-contract defaults (30s/10s) so a regression to the
        // default is caught, and a pinned kill date so it survives the round trip.
        var sleep = TimeSpan.FromSeconds(45);
        var jitter = TimeSpan.FromSeconds(15);
        var killDate = new DateTimeOffset(2027, 1, 31, 12, 0, 0, TimeSpan.Zero);
        var @params = new BuildParams(
            EngagementId.New(),
            OperatorId.New(),
            ImplantClass.Stage2,
            new TargetProfile("linux", "amd64"),
            new TransportProfile("http://c2.example.test/implants/enroll", "/beacon"),
            new BeaconProfile(sleep, jitter, killDate));

        var baked = DotNetBuildUnit.RenderBakedProfile(@params);

        using var doc = JsonDocument.Parse(Base64UrlDecode(baked));
        var root = doc.RootElement;

        Assert.Equal("45s", root.GetProperty("sleep").GetString());
        Assert.Equal("15s", root.GetProperty("jitter").GetString());
        Assert.Equal(killDate.ToString("O"), root.GetProperty("killDate").GetString());
    }

    [Fact]
    public void RenderBakedProfile_BakesTheConfiguredTransportProfile()
    {
        // The malleable transport profile (architecture.md Sec 7) must land
        // in the .NET unit's baked profile the same way it lands in the Go unit's
        // -- the cross-unit encoding test already proves byte-identity, this
        // asserts the decoded values directly so a .NET-only regression is caught.
        var transport = new TransportProfile("http://c2.example.test/implants/enroll", "/beacon")
        {
            EnrollPath = "/api/v1/health",
            UserAgent = "Mozilla/5.0 (RodTest)",
            Headers = new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "10.0.0.1",
                ["Accept"] = "application/json",
            },
            RequestTimeout = TimeSpan.FromSeconds(12),
            Envelope = TransportEnvelope.Base64,
        };
        var @params = new BuildParams(
            EngagementId.New(),
            OperatorId.New(),
            ImplantClass.Stage2,
            new TargetProfile("linux", "amd64"),
            transport,
            new BeaconProfile(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddDays(30)));

        var baked = DotNetBuildUnit.RenderBakedProfile(@params);

        using var doc = JsonDocument.Parse(Base64UrlDecode(baked));
        var root = doc.RootElement;

        Assert.Equal("/api/v1/health", root.GetProperty("enrollPath").GetString());
        Assert.Equal("Mozilla/5.0 (RodTest)", root.GetProperty("userAgent").GetString());
        Assert.Equal("12s", root.GetProperty("requestTimeout").GetString());
        Assert.Equal("base64", root.GetProperty("envelope").GetString());
        Assert.Equal("10.0.0.1", root.GetProperty("headers").GetProperty("X-Forwarded-For").GetString());
        Assert.Equal("application/json", root.GetProperty("headers").GetProperty("Accept").GetString());
    }

    [Fact]
    public void RenderBakedProfile_BakesContactSealingOnlyWhenTheKeyRides()
    {
        // Contact protection is its own knob beside the enroll-body envelope
        // (architecture.md Sec 8/9): "aesgcm" seals every contact body under
        // the per-artifact key, "none" is the lab-debug plaintext frame. The
        // seal bakes only when the key rides the params too -- a protection
        // ask with no key never bakes a seal the artifact cannot honor, and
        // an explicit opt-out stays opted out even when a key happens to ride
        // (the AesGcm enroll envelope mints one for the enroll body alone).
        var (keyId, key) = (Guid.NewGuid(), RandomNumberGenerator.GetBytes(32));
        var @params = Params() with { EnvelopeKeyId = keyId, EnvelopeKey = key };

        using var sealedDoc = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(@params)));
        Assert.Equal("aesgcm", sealedDoc.RootElement.GetProperty("contactEnvelope").GetString());

        // The default transport profile carries no key here (the mint is the
        // transport endpoint's), so a unit-level build bakes the plaintext
        // shape -- and so does a params pair whose protection was turned off.
        using var plainDoc = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(Params())));
        Assert.Equal("none", plainDoc.RootElement.GetProperty("contactEnvelope").GetString());

        var optedOut = Params() with
        {
            Transport = new TransportProfile("http://c2.example.test/implants/enroll", "/beacon")
            {
                ContactProtection = false,
            },
            EnvelopeKeyId = keyId,
            EnvelopeKey = key,
        };
        using var outDoc = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(optedOut)));
        Assert.Equal("none", outDoc.RootElement.GetProperty("contactEnvelope").GetString());
    }

    [Fact]
    public void RenderBakedProfile_BakesTheSplitBeaconHostWhenNamed()
    {
        // The split-socket shape (architecture.md Sec 8): enroll dials the
        // cleartext listener, the gRPC beacon the mTLS one, so the baked
        // beaconURL must carry the named beacon host verbatim -- deriving it
        // from the enroll endpoint would bake a beacon onto a socket that
        // cannot carry HTTP/2. Without a named beacon the derived
        // single-front bake stays exactly as it was.
        var split = Params() with
        {
            Transport = new TransportProfile("http://c2.example.test/implants/enroll", "/beacon")
            {
                BeaconEndpoint = "https://mtls.example.test",
            },
        };

        using var splitDoc = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(split)));
        Assert.Equal("https://mtls.example.test", splitDoc.RootElement.GetProperty("beaconURL").GetString());

        using var plain = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(Params())));
        Assert.Equal("http://c2.example.test", plain.RootElement.GetProperty("beaconURL").GetString());
    }

    [Fact]
    public void RenderBakedProfile_BakesThePinnedCaWhenTheProfileCarriesOne()
    {
        // The pinned teamserver CA rides the profile verbatim, so the
        // artifact's first contact (enroll, before any certificate of its
        // own) validates the server against the C2's CA -- the single-port
        // https shape needs exactly this trust anchor. An empty pin keeps
        // system/default validation.
        const string pem = "-----BEGIN CERTIFICATE-----\nZm9v\n-----END CERTIFICATE-----\n";

        var pinned = Params() with
        {
            Transport = new TransportProfile("http://c2.example.test/implants/enroll", "/beacon")
            {
                CaPem = pem,
            },
        };
        using var pinnedDoc = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(pinned)));
        Assert.Equal(pem, pinnedDoc.RootElement.GetProperty("caCert").GetString());

        using var plain = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(Params())));
        Assert.Equal(string.Empty, plain.RootElement.GetProperty("caCert").GetString());
    }

    [Fact]
    public void RenderBakedProfile_BakesTheOrderedFallbackEndpoints()
    {
        // The fallback egress list (architecture.md Sec 8) rides as a JSON array
        // in walk order behind the primary enrollURL, and the key is always
        // present -- [] when the build names none -- so every build unit emits
        // the same key set and an implant of any language decodes the same walk.
        var transport = new TransportProfile("http://c2.example.test/implants/enroll", "/beacon")
        {
            FallbackEndpoints = new[]
            {
                "https://alt1.example.test/implants/enroll",
                "https://alt2.example.test/implants/enroll",
            },
        };
        var @params = new BuildParams(
            EngagementId.New(),
            OperatorId.New(),
            ImplantClass.Stage2,
            new TargetProfile("linux", "amd64"),
            transport,
            new BeaconProfile(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddDays(30)));

        using var doc = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(@params)));
        var fallbacks = doc.RootElement.GetProperty("fallbackEnrollURLs");
        Assert.Equal(JsonValueKind.Array, fallbacks.ValueKind);
        Assert.Equal(2, fallbacks.GetArrayLength());
        Assert.Equal("https://alt1.example.test/implants/enroll", fallbacks[0].GetString());
        Assert.Equal("https://alt2.example.test/implants/enroll", fallbacks[1].GetString());

        using var plain = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(Params())));
        var empty = plain.RootElement.GetProperty("fallbackEnrollURLs");
        Assert.Equal(JsonValueKind.Array, empty.ValueKind);
        Assert.Equal(0, empty.GetArrayLength());
    }

    [Fact]
    public void RenderBakedProfile_BakesTheEnrollmentCredentialWhenMinted()
    {
        // The build's minted token rides the profile as the "token" key, so
        // the artifact deploys with zero run-time arguments and spends the
        // credential at its own enroll. A credential-free build (no mint)
        // omits the key entirely -- the same profile it always baked.
        var @params = Params() with { TokenSecret = "build-minted-secret" };

        using var baked = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(@params)));
        Assert.Equal("build-minted-secret", baked.RootElement.GetProperty("token").GetString());

        using var plain = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(Params())));
        Assert.False(plain.RootElement.TryGetProperty("token", out _));
    }

    [Fact]
    public void RenderBakedProfile_BakesTheEnvelopeKeyWhenTheEnvelopeIsEncrypted()
    {
        // The AES-GCM envelope's key pair rides as one base64 value --
        // keyId(16) || key(32) -- beside the "aesgcm" envelope name, so the
        // implant encrypts its enroll body under the key the teamserver
        // recorded. Every other envelope omits the key entirely.
        var keyId = Guid.NewGuid();
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var @params = Params() with
        {
            Transport = new TransportProfile("http://c2.example.test", "/beacon")
            {
                Envelope = TransportEnvelope.AesGcm,
            },
            EnvelopeKeyId = keyId,
            EnvelopeKey = key,
        };

        using var baked = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(@params)));
        Assert.Equal("aesgcm", baked.RootElement.GetProperty("envelope").GetString());
        var bakedKey = baked.RootElement.GetProperty("envelopeKey").GetString();
        Assert.NotNull(bakedKey);
        var packed = Convert.FromBase64String(bakedKey!);
        Assert.Equal(16 + 32, packed.Length);
        Assert.Equal(keyId, new Guid(packed[..16].ToArray()));
        Assert.Equal(key, packed[16..].ToArray());

        using var plain = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderBakedProfile(Params())));
        Assert.False(plain.RootElement.TryGetProperty("envelopeKey", out _));
    }

    [Fact]
    public void RenderStagerProfile_BakesTheFetchCredentialWhenMinted()
    {
        // The stager presents its own minted token for the fetch (verified,
        // never spent); the stage-2 it launches spends the token baked into
        // the stage-2's own profile.
        var @params = Params(ImplantClass.Stager) with
        {
            Stage2 = new Stage2Payload(Guid.NewGuid(), "abc123"),
            TokenSecret = "stager-fetch-secret",
        };

        using var baked = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderStagerProfile(@params)));
        Assert.Equal("stager-fetch-secret", baked.RootElement.GetProperty("token").GetString());

        var plainParams = Params(ImplantClass.Stager) with { Stage2 = new Stage2Payload(Guid.NewGuid(), "abc123") };
        using var plain = JsonDocument.Parse(Base64UrlDecode(DotNetBuildUnit.RenderStagerProfile(plainParams)));
        Assert.False(plain.RootElement.TryGetProperty("token", out _));
    }

    [Theory]
    [InlineData("linux", "amd64", "linux-x64")]
    [InlineData("linux", "x86_64", "linux-x64")]
    [InlineData("linux", "arm64", "linux-arm64")]
    [InlineData("windows", "amd64", "win-x64")]
    [InlineData("win", "386", "win-x86")]
    [InlineData("osx", "aarch64", "osx-arm64")]
    [InlineData("darwin", "x64", "osx-x64")]
    public void MapRid_MapsContractTargetsOntoRuntimeIdentifiers(
        string os, string arch, string rid)
    {
        // The build contract speaks Go-style os/arch pairs; the publish step
        // speaks RIDs. Every spelling an operator sends must land on one.
        Assert.Equal(rid, DotNetBuildUnit.MapRid(new TargetProfile(os, arch)));
    }

    [Theory]
    [InlineData("plan9", "amd64")]
    [InlineData("linux", "riscv")]
    public void MapRid_RejectsAnUnmappableTarget_WithTheSupportedSetNamed(string os, string arch)
    {
        // An unmappable target fails at build time with a fixable message, not
        // a silent fallback to the build host's platform.
        var ex = Assert.Throws<InvalidOperationException>(
            () => DotNetBuildUnit.MapRid(new TargetProfile(os, arch)));
        Assert.Contains("supported", ex.Message);
    }

    // RFC 4648 base64url without padding, matching DotNetBuildUnit.Base64Url.
    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) & ~3, '=');
        return Convert.FromBase64String(padded);
    }

    [Fact]
    public void RenderStagerProfile_BakesTheFetchReference()
    {
        // The stager's profile is the fetch contract (architecture.md Sec 6):
        // the listener to fetch from, the payload id, and the sha256 the loader
        // verifies the fetched bytes against -- the fingerprint the operator
        // saw at stage-2 build time, so a tampered fetch is refused.
        var payloadId = Guid.NewGuid();
        var @params = Params(ImplantClass.Stager) with
        {
            Stage2 = new Stage2Payload(payloadId, "abc123"),
        };

        var baked = DotNetBuildUnit.RenderStagerProfile(@params);

        using var doc = JsonDocument.Parse(Base64UrlDecode(baked));
        var root = doc.RootElement;
        Assert.Equal("http://c2.example.test/implants/enroll", root.GetProperty("enrollURL").GetString());
        Assert.Equal(payloadId.ToString(), root.GetProperty("stage2PayloadId").GetString());
        Assert.Equal("abc123", root.GetProperty("stage2Sha256").GetString());
        Assert.Equal(@params.Beacon.KillDate!.Value.ToString("O"), root.GetProperty("killDate").GetString());
    }

    [Fact]
    public void RenderStagerProfile_RefusesANonStagerClassOrMissingReference()
    {
        Assert.Throws<InvalidOperationException>(
            () => DotNetBuildUnit.RenderStagerProfile(Params(ImplantClass.Stage2)));

        var missing = Params(ImplantClass.Stager);
        Assert.Throws<InvalidOperationException>(
            () => DotNetBuildUnit.RenderStagerProfile(missing));
    }

    [DotNetFact]
    public async Task StagerBuild_ReturnsNonEmptyArtifact()
    {
        // The stager output class compiles the loader tree, not the implant
        // tree (architecture.md Sec 6): the artifact is a real, fingerprinted
        // stage-1 executable with the fetch reference baked in.
        var unit = new DotNetBuildUnit();
        var @params = Params(ImplantClass.Stager) with
        {
            Stage2 = new Stage2Payload(Guid.NewGuid(), "abc123"),
        };

        var artifact = await unit.BuildAsync(@params);

        Assert.Equal(Language.DotNet, artifact.Language);
        Assert.Equal(ImplantClass.Stager, artifact.Params.Class);
        Assert.NotEmpty(artifact.Content);
        var expected = Convert.ToHexString(SHA256.HashData(artifact.Content)).ToLowerInvariant();
        Assert.Equal(expected, artifact.Fingerprint);
    }

    [DotNetFact]
    public async Task Build_ReturnsNonEmptyArtifact_WithDotNetLanguage()
    {
        var unit = new DotNetBuildUnit();

        var artifact = await unit.BuildAsync(Params());

        Assert.Equal(Language.DotNet, artifact.Language);
        Assert.NotEmpty(artifact.Content);
        Assert.Equal(artifact.Content.Length, artifact.Size);
        Assert.Equal("application/octet-stream", artifact.ContentType);
    }

    [Fact]
    public async Task Build_AMissingExtensionDirectoryFailsLoudly()
    {
        // The loud-failure rule: a configured-but-missing directory aborts the
        // build before any compile, so an operator never receives an artifact
        // that silently lacks the handlers they believe it carries.
        var unit = new DotNetBuildUnit(
            extensionDir: Path.Combine(Path.GetTempPath(), "rod-ext-missing-" + Guid.NewGuid().ToString("N")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.BuildAsync(Params()));
    }

    [DotNetFact]
    public async Task Build_AnEmptyExtensionDirectoryIsTheUnsetShape()
    {
        // The shipped appsettings carries Build:ImplantExtensionDirectory as an
        // empty string, which is the unset shape everywhere else on the config
        // surface. The unit must read it the same way, not as a configured
        // directory: surfaced by the rehearsal walk, where every implant-class
        // build against the shipped configuration failed on the empty value.
        var unit = new DotNetBuildUnit(extensionDir: "");

        var artifact = await unit.BuildAsync(Params());

        Assert.Equal(Language.DotNet, artifact.Language);
        Assert.NotEmpty(artifact.Content);
    }

    [DotNetFact]
    public async Task Build_AnEmptySourceDirectoryIsTheUnsetShape()
    {
        // Same whitespace-is-absent rule as the extension directory: an
        // installed teamserver that names only one build source tree (or
        // carries the empty strings a templated config can produce) still
        // resolves both from the repo walk-up. Surfaced by walking payload
        // builds through the installed, supervised shape.
        var unit = new DotNetBuildUnit(implantSourceDir: "", stagerSourceDir: "");

        var artifact = await unit.BuildAsync(Params());

        Assert.Equal(Language.DotNet, artifact.Language);
        Assert.NotEmpty(artifact.Content);
    }

    [DotNetFact]
    public async Task Build_WithExtensionDirectory_CompilesTheOverlayIn()
    {
        // The extension kit's acceptance, compile leg: a handler source dropped
        // into the configured directory builds into the artifact -- the overlay
        // copies the sources, the generated registrations compile against the
        // implant tree, and dotnet publish produces the artifact. The run leg --
        // the artifact advertising and running the verb at handshake -- lives in
        // the integration suite (ExtensionKitEndToEndTests).
        var extensionDir = Path.Combine(Path.GetTempPath(), "rod-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extensionDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(extensionDir, "DemoPingHandler.cs"), """
                using Rod.Implant.Internal;
                using Rod.V1;

                namespace MyTradecraft.Demo;

                internal sealed class DemoPingHandler : ICapabilityHandler
                {
                    public string Verb => "demo.ping";

                    public HandlerResult Handle(string arguments)
                        => (TaskOutcome.Succeeded, "pong");
                }
                """);

            var unit = new DotNetBuildUnit(extensionDir: extensionDir);

            var artifact = await unit.BuildAsync(Params());

            Assert.Equal(Language.DotNet, artifact.Language);
            Assert.NotEmpty(artifact.Content);
        }
        finally
        {
            try { Directory.Delete(extensionDir, recursive: true); } catch { }
        }
    }

    [DotNetFact]
    public async Task Build_AClassExcludingKeylogging_CompilesWithoutTheKeylogHandler()
    {
        // The handler trim's acceptance, against the verb it names: an
        // out-of-tree keylog handler (a stand-in that refuses to run -- the
        // real tradecraft stays out-of-tree by the Sec 13 boundary) is gated
        // to the stage-2 class, so a pivot build must compile with the
        // handler's source and registration left out entirely. The staging
        // rewrite is pinned file by file in HandlerModuleSelectionTests and
        // the overlay's filter in ImplantExtensionOverlayTests; this leg
        // proves the trimmed tree still publishes a real artifact. The
        // operator's own verb (no class lists it) rides along as before.
        var extensionDir = Path.Combine(Path.GetTempPath(), "rod-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extensionDir);
        try
        {
            await WriteKeylogKit(extensionDir);

            var unit = new DotNetBuildUnit(extensionDir: extensionDir);

            var artifact = await unit.BuildAsync(Params(ImplantClass.Pivot));

            Assert.Equal(Language.DotNet, artifact.Language);
            Assert.NotEmpty(artifact.Content);
        }
        finally
        {
            try { Directory.Delete(extensionDir, recursive: true); } catch { }
        }
    }

    [DotNetFact]
    public async Task Build_AFullClass_KeepsTheExtensionKeylogHandler()
    {
        // The trim's other half: the stage-2 class carries collect.keylog, so
        // the same kit builds with the keylog handler compiled in and
        // registered -- a full-class build is the unchanged shape.
        var extensionDir = Path.Combine(Path.GetTempPath(), "rod-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extensionDir);
        try
        {
            await WriteKeylogKit(extensionDir);

            var unit = new DotNetBuildUnit(extensionDir: extensionDir);

            var artifact = await unit.BuildAsync(Params(ImplantClass.Stage2));

            Assert.Equal(Language.DotNet, artifact.Language);
            Assert.NotEmpty(artifact.Content);
        }
        finally
        {
            try { Directory.Delete(extensionDir, recursive: true); } catch { }
        }
    }

    [DotNetFact]
    public async Task Build_AReducedClass_ProducesAnArtifact()
    {
        // The reference-side trim against the most reduced real shape: a
        // pivot build compiles the tunnel sources alone against the
        // generated HandlerSelection and still publishes -- the trim leaves
        // a compilation that stands up whole.
        var unit = new DotNetBuildUnit();

        var artifact = await unit.BuildAsync(Params(ImplantClass.Pivot));

        Assert.Equal(Language.DotNet, artifact.Language);
        Assert.NotEmpty(artifact.Content);
    }

    // The keylog kit the trim acceptance builds against: a stand-in handler
    // that refuses to run (input capture itself stays out-of-tree, Sec 13)
    // plus the operator's own ungated verb.
    private static async Task WriteKeylogKit(string extensionDir)
    {
        await File.WriteAllTextAsync(Path.Combine(extensionDir, "KeylogHandler.cs"), """
            using Rod.Implant.Internal;
            using Rod.V1;

            namespace Kit.Collect;

            internal sealed class KeylogHandler : ICapabilityHandler
            {
                public string Verb => "collect.keylog";

                public HandlerResult Handle(string arguments)
                    => (TaskOutcome.Failed, "keylog stand-in: no input capture in tests");
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(extensionDir, "DemoPingHandler.cs"), """
            using Rod.Implant.Internal;
            using Rod.V1;

            namespace Kit.Demo;

            internal sealed class DemoPingHandler : ICapabilityHandler
            {
                public string Verb => "demo.ping";

                public HandlerResult Handle(string arguments)
                    => (TaskOutcome.Succeeded, "pong");
            }
            """);
    }

    [DotNetFact]
    public async Task Build_Fingerprint_MatchesSha256OfContent()
    {
        var unit = new DotNetBuildUnit();

        var artifact = await unit.BuildAsync(Params());

        var expected = Convert.ToHexString(SHA256.HashData(artifact.Content)).ToLowerInvariant();
        Assert.Equal(expected, artifact.Fingerprint);
        Assert.Equal(64, artifact.Fingerprint.Length);
    }

    [DotNetFact]
    public async Task TwoBuilds_WithIdenticalParams_ProduceDifferentFingerprints()
    {
        // Each build bakes a fresh artifact over a fresh temp dir; the .NET
        // compiler embeds non-determinism (build timestamp, MVID), so two builds
        // never share a fingerprint -- matching the per-implant uniqueness
        // contract (architecture.md Sec 5.1/6).
        var unit = new DotNetBuildUnit();

        var first = await unit.BuildAsync(Params());
        var second = await unit.BuildAsync(Params());

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [DotNetFact]
    public async Task Build_AWebShapedArtifact_CarriesNoGrpcClient()
    {
        // The transport trim's acceptance (architecture.md Sec 8): a build
        // whose egress walk holds only web fronts compiles no stream module,
        // so the artifact links no gRPC client at all -- the proto generation
        // carried the rod.v1 message types only and the single-file bundle
        // embeds no Grpc.Net.Client assembly.
        var unit = new DotNetBuildUnit();

        var artifact = await unit.BuildAsync(Params());

        Assert.NotEmpty(artifact.Content);
        Assert.False(Contains(artifact.Content, "Grpc.Net.Client.dll"u8),
            "a web-shaped artifact must not embed the gRPC client library");
    }

    [DotNetFact]
    public async Task Build_AnMtlsShapedArtifact_KeepsTheGrpcClient()
    {
        // The trim's other half: a walk whose primary is the bare-authority
        // mTLS socket keeps the stream module, and with it the gRPC client
        // the stream dials.
        var unit = new DotNetBuildUnit();
        var @params = Params() with
        {
            Transport = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
            {
                BeaconEndpoint = "c2.example.test:8443",
            },
        };

        var artifact = await unit.BuildAsync(@params);

        Assert.NotEmpty(artifact.Content);
        Assert.True(Contains(artifact.Content, "Grpc.Net.Client.dll"u8),
            "an mTLS-shaped artifact keeps the gRPC client the stream dials");
    }

    [DotNetFact]
    public async Task Build_TheTrimmedFormat_PublishesAMateriallySmallerExecutable()
    {
        // The trimmed format keeps the drop-and-run shape (a native
        // single-file executable) with IL trimming applied, so the artifact
        // transfers for a fraction of the default's bytes. The strict
        // inequality is the point: a trimmed build that stopped being
        // smaller would mean the trim stopped running.
        var unit = new DotNetBuildUnit();

        var singleFile = await unit.BuildAsync(Params());
        var trimmed = await unit.BuildAsync(Params() with { Format = ArtifactFormat.TrimmedExe });

        Assert.Equal("application/octet-stream", trimmed.ContentType);
        Assert.NotEmpty(trimmed.Content);
        Assert.True(trimmed.Size < singleFile.Size,
            $"the trimmed executable ({trimmed.Size} bytes) must be smaller than the single-file default ({singleFile.Size} bytes)");
    }

    [DotNetFact]
    public async Task Build_TheNativeAotFormat_PublishesARuntimeFreeExecutable()
    {
        // The AOT format compiles ahead of time to a native binary with no
        // runtime to bundle or bootstrap -- the same deployment property a C
        // or Go artifact has, at a fraction of the trimmed bundle's bytes
        // (measured on linux-x64: 6.2 MB stager and 10.3 MB implant, against
        // 14 MB trimmed and 37-40 MB single-file). The strict inequality
        // against the trimmed format pins that the AOT pass actually ran.
        var unit = new DotNetBuildUnit();

        var stager = await unit.BuildAsync(Params(ImplantClass.Stager) with
        {
            Stage2 = new Stage2Payload(Guid.NewGuid(), "abc123"),
            Format = ArtifactFormat.NativeAot,
        });
        var implant = await unit.BuildAsync(Params() with { Format = ArtifactFormat.NativeAot });
        var trimmed = await unit.BuildAsync(Params() with { Format = ArtifactFormat.TrimmedExe });

        Assert.Equal("application/octet-stream", stager.ContentType);
        Assert.True(stager.Size < trimmed.Size,
            $"the AOT stager ({stager.Size} bytes) must be smaller than the trimmed implant ({trimmed.Size} bytes)");
        Assert.True(implant.Size < 40_000_000, "the AOT implant is a native binary, not a bundled runtime");

        // On a Linux host the built artifacts prove they are native code the
        // loader accepts: both run, report the missing baked profile, and
        // exit 2 -- the fielded shape's only console output.
        AssertNativeRunsAndRefusesWithoutABake(stager.Content, "rod-stager");
        AssertNativeRunsAndRefusesWithoutABake(implant.Content, "rod-implant");
    }

    // Runs a built native artifact from a temp file and asserts the no-bake
    // refusal: the exact fatal one-liner and exit code 2 the fielded shape
    // prints. Proves the bytes are a working native executable, not just a
    // smaller file. Linux only -- the build targets linux-x64 native code.
    private static void AssertNativeRunsAndRefusesWithoutABake(byte[] content, string name)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var path = Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, content);
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            using var process = System.Diagnostics.Process.Start(start)!;
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            Assert.Equal(2, process.ExitCode);
            Assert.Contains("no baked profile", stderr);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    [DotNetFact]
    public async Task Build_TheDllFormat_ShipsAZippedFrameworkDependentBundle()
    {
        // The dll format (architecture.md Sec 6) is the in-memory-loadable
        // shape: one zip carrying the entry assembly, its dependencies, and
        // the deps/runtimeconfig files, published framework-dependent against
        // net8.0 so every supported host runtime (.NET 8 pwsh through the
        // teamserver's own 10) loads the same bytes. The default profile's
        // egress walk is web-shaped, so the transport trim keeps the gRPC
        // client out of the bundle the same way it keeps it out of the
        // single-file build.
        var unit = new DotNetBuildUnit();

        var artifact = await unit.BuildAsync(Params() with { Format = ArtifactFormat.Dll });

        Assert.Equal("application/zip", artifact.ContentType);
        Assert.Equal(artifact.Content.Length, artifact.Size);
        // A zip opens with the local-file-header signature.
        Assert.True(artifact.Content.Length > 4
            && artifact.Content[0] == (byte)'P' && artifact.Content[1] == (byte)'K'
            && artifact.Content[2] == 3 && artifact.Content[3] == 4);

        using var archive = new System.IO.Compression.ZipArchive(
            new MemoryStream(artifact.Content));
        var names = archive.Entries.Select(e => e.Name).ToHashSet();
        Assert.Contains("Rod.Implant.dll", names);
        Assert.Contains("Rod.Implant.runtimeconfig.json", names);
        Assert.Contains("Rod.Implant.deps.json", names);
        Assert.Contains("Google.Protobuf.dll", names);
        Assert.DoesNotContain("Grpc.Net.Client.dll", names);
    }

    // Scans the single-file bundle's bytes for a pattern. The bundle manifest
    // lists every embedded file's name as plain text (only the payloads are
    // compressed), so an assembly's presence in the bundle is visible in its
    // name alone.
    private static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        }
        return false;
    }
}
