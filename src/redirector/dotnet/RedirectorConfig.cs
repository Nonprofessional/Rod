using System.Net;

namespace Rod.Redirector;

// Carries the single forwarding rule the reference redirector runs with
// (architecture.md Sec 8, ADR 0011). The full design lets an operator run one
// process per fronted port -- near-stateless, cheap, and more robust than one
// process holding several listeners (a burned port does not drag the others
// down) -- so a single rule covers the canonical "one redirector fronts one
// listener" deployment. Multi-port fronting is one process per port, not one
// rule list here.

/// <summary>
/// Everything the reference redirector needs to forward one TCP endpoint to an
/// upstream teamserver listener. Mirrors the implant's hand-rolled config.Config:
/// flags win over the matching ROD_* env, required fields that stay empty after
/// both are rejected with a usage error, and -h prints usage and exits.
/// </summary>
internal sealed class RedirectorConfig
{
    /// <summary>
    /// The bind host (a literal IP, or "*" / "0.0.0.0" / "::" for any). DNS
    /// hostnames are not supported for the bind side -- a bind needs an interface
    /// address -- so a name is rejected at validation. See <see cref="ListenAddress"/>.
    /// </summary>
    public string ListenHost { get; private set; } = string.Empty;

    /// <summary>The TCP port to listen on.</summary>
    public int ListenPort { get; private set; }

    /// <summary>
    /// The upstream host (DNS name or literal IP) the redirector splices to --
    /// typically the teamserver listener's bind address.
    /// </summary>
    public string UpstreamHost { get; private set; } = string.Empty;

    /// <summary>The upstream TCP port to splice to.</summary>
    public int UpstreamPort { get; private set; }

    /// <summary>
    /// The source-IP allow-list. Empty (the default) allows every source: the
    /// redirector is typically fronted by a VPS firewall and the real identity
    /// gate is the teamserver's mTLS handshake (architecture.md Sec 9), so the
    /// allow-list is a deployment-time tightening, not the security boundary.
    /// </summary>
    public CidrAllowList Allow { get; private set; } = CidrAllowList.AllowAll;

    /// <summary>
    /// The resolved bind address. "*" / "0.0.0.0" resolve to IPv4 any, "::" /
    /// "::0" to IPv6 any; any other value must parse as a literal IP address (a
    /// DNS hostname here is a config error -- the bind side needs an address).
    /// </summary>
    public IPAddress ListenAddress =>
        ListenHost is "*" or "0.0.0.0" ? IPAddress.Any
        : ListenHost is "::" or "::0" ? IPAddress.IPv6Any
        : IPAddress.Parse(ListenHost);

    /// <summary>
    /// Builds a RedirectorConfig from command-line flags, falling back to the
    /// matching ROD_* environment variable for each. Flags win over env. Required
    /// fields that are still empty after both are rejected with a usage error.
    /// </summary>
    public static RedirectorConfig Parse(string[] args)
    {
        var config = new RedirectorConfig
        {
            ListenHost = Env("ROD_LISTEN", string.Empty),
            UpstreamHost = Env("ROD_UPSTREAM", string.Empty),
        };
        var allowRaw = Env("ROD_ALLOW", string.Empty);

        // Hand-rolled parse: no System.CommandLine dependency, matching the
        // "no ceremony" style of the implant's flag set. Each flag takes the next
        // arg as its value; -h prints usage and exits with code 2.
        for (var i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            switch (flag)
            {
                case "-h":
                case "--help":
                    Console.Error.WriteLine(Usage);
                    throw new ExitProgramException(2);
                case "-listen":
                case "--listen":
                    config.ListenHost = TakeValue(args, ref i, flag);
                    break;
                case "-upstream":
                case "--upstream":
                    config.UpstreamHost = TakeValue(args, ref i, flag);
                    break;
                case "-allow":
                case "--allow":
                    allowRaw = TakeValue(args, ref i, flag);
                    break;
                default:
                    throw new ExitProgramException(2, $"unknown flag: {flag}\n{Usage}");
            }
        }

        try
        {
            config.Allow = CidrAllowList.Parse(allowRaw.Length == 0 ? null : new[] { allowRaw });
        }
        catch (ArgumentException ex)
        {
            throw new ExitProgramException(2, $"-allow: {ex.Message}\n{Usage}");
        }

        config.Validate();
        return config;
    }

    /// <summary>
    /// Enforces the required fields and splits host:port for each endpoint. The
    /// listen host must resolve to a bindable address now, so a bad bind fails at
    /// startup with a clear message instead of inside the accept loop.
    /// </summary>
    private void Validate()
    {
        var missing = new List<string>();
        if (ListenHost.Length == 0)
            missing.Add("-listen/ROD_LISTEN");
        if (UpstreamHost.Length == 0)
            missing.Add("-upstream/ROD_UPSTREAM");
        if (missing.Count > 0)
            throw new ExitProgramException(2, $"missing required config: {string.Join(", ", missing)}\n{Usage}");

        (ListenHost, ListenPort) = SplitHostPort(ListenHost, "listen");
        (UpstreamHost, UpstreamPort) = SplitHostPort(UpstreamHost, "upstream");

        try
        {
            _ = ListenAddress;
        }
        catch (FormatException)
        {
            throw new ExitProgramException(
                2, $"listen: '{ListenHost}' is not a bindable IP address (use * for any)\n{Usage}");
        }
    }

    // Splits "host:port" into host and port. IPv6 literals must be bracketed
    // ([::1]:443) so the trailing :port is unambiguous; a bare IPv6 literal with
    // colons would otherwise split wrong.
    private static (string Host, int Port) SplitHostPort(string raw, string field)
    {
        if (raw.StartsWith('['))
        {
            var close = raw.IndexOf(']');
            if (close < 0 || close + 1 >= raw.Length || raw[close + 1] != ':')
                throw new ExitProgramException(2, $"{field}: '{raw}' is not [host]:port\n{Usage}");
            var host = raw[1..close];
            var port = ParsePort(raw[(close + 2)..], field);
            return (host, port);
        }

        var colon = raw.LastIndexOf(':');
        if (colon <= 0 || colon == raw.Length - 1)
            throw new ExitProgramException(2, $"{field}: '{raw}' is not host:port\n{Usage}");
        return (raw[..colon], ParsePort(raw[(colon + 1)..], field));
    }

    private static int ParsePort(string text, string field)
    {
        if (!int.TryParse(text, out var port) || port < 1 || port > 65535)
            throw new ExitProgramException(2, $"{field}: '{text}' is not a valid port\n{Usage}");
        return port;
    }

    private const string Usage = """
        usage: rod-redirector [flags]

          -listen string    bind endpoint (host:port; host may be * for any, or a literal IP)
          -upstream string  teamserver listener endpoint (host:port; host may be a DNS name)
          -allow string     optional comma-separated source CIDR allow-list (e.g. 10.0.0.0/8)

        Each flag falls back to the matching ROD_LISTEN / ROD_UPSTREAM / ROD_ALLOW
        environment variable. The redirector forwards opaque TCP bytes; it never
        terminates transport. Swap a burned redirector by deploying a fresh one
        and repointing the listener (POST /listeners/{id}:repoint).
        """;

    private static string Env(string key, string fallback)
        => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    // Returns the value for the flag at index i, advancing i past it. Throws a
    // usage error when the flag has no following value.
    private static string TakeValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ExitProgramException(2, $"flag needs an argument: {flag}\n{Usage}");
        i++;
        return args[i];
    }
}

/// <summary>
/// Thrown to request a clean exit with a specific code and message (usage error
/// or -h). The program's top level catches it, prints the message, and exits.
/// Mirrors the implant's ExitProgramException so the two binaries behave the same
/// way on a bad flag.
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
