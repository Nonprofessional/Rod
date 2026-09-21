using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Pki;
using Rod.Transport.Listeners;
using Rod.Transport.Listeners.Providers;

namespace Rod.Transport.Payloads;

/// <summary>
/// Shared parsing and validation for a payload-build request body: the wire
/// DTO (<see cref="Endpoints.PayloadEndpoints.BuildPayloadRequest"/>) into the
/// build pipeline's <see cref="BuildRequest"/>. Both build paths -- the
/// synchronous <c>POST /payloads</c> and the background
/// <c>POST /payload-jobs</c> -- accept the same body and must refuse the same
/// things, so the validation lives here once. Every refusal is an operator
/// mistake the build must not silently paper over (a payload that phones
/// nowhere, a low-and-slow ask built interactive).
/// </summary>
internal static class PayloadBuildRequestParser
{
    private static readonly TimeSpan DefaultSleep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultJitter = TimeSpan.FromSeconds(10);

    // Upper bound for operator-supplied sleep/jitter: a beacon interval beyond a
    // year is nonsense, and an unbounded double would overflow
    // TimeSpan.FromSeconds into a 500. The clamp keeps the request a clean 4xx
    // class instead.
    private const double MaxDurationSeconds = 31_536_000; // 1 year

    /// <summary>
    /// Parses the body into a <see cref="BuildRequest"/>, or returns an error
    /// string naming the first refusal. The stager stage-2 reference resolves
    /// against the payload store here so the build contract carries a verified
    /// reference, never a raw operator string; a named listener resolves to
    /// its public endpoint the same way. The teamserver's CA is baked into the
    /// profile as the pin the artifact's first contact validates against.
    /// </summary>
    public static async Task<(BuildRequest? Request, string? Error)> ParseAsync(
        Endpoints.PayloadEndpoints.BuildPayloadRequest body,
        EngagementId engagementId,
        OperatorId requestedBy,
        IListenerRegistry listeners,
        IImplantCertificateAuthority ca,
        CancellationToken cancellationToken)
    {
        // Language and class come in as strings and parse to the enums; anything
        // that does not parse is a 400. The defaults keep a minimal request
        // valid (the in-tree .NET unit, a stage-2 implant, linux/amd64).
        if (!TryParseLanguage(body.Language, out var language))
            return (null, "Language is not recognized.");
        if (!TryParseClass(body.Class, out var @class))
            return (null, "Implant class is not recognized.");
        // The stager class is retired with the .NET trees: delivery rides the
        // launcher one-liners (the disk families plus the in-memory memfd
        // family), which fetch the stage-2 over the same token-gated route a
        // loader ever used.
        if (@class == ImplantClass.Stager)
            return (null,
                "The stager class is retired; deliver the stage-2 through the launcher one-liners (launchers render them per payload).");
        // The dll bundle was the .NET in-memory shape; with the .NET implant
        // retired there is no producer -- the Rust implant is native in every
        // format, and its 'aot' spelling is the one the memfd one-liner
        // family keys on.
        if (!ArtifactFormats.TryParse(body.Format, out var format))
            return (null, "Format must be one of 'exe' (the default), 'exe-trimmed', or 'aot'.");
        if (format == ArtifactFormat.Dll)
            return (null,
                "The dll format is retired with the .NET implant; every Rust artifact is a native executable -- use 'exe' or 'aot'.");

        // The endpoint list is what the baked implant dials, so a malformed
        // entry must not reach the build: it would not fail there -- it would
        // produce a payload that phones nowhere, the silent kind of failure
        // an operator discovers on target. A named listener supplies the
        // endpoint from its own record, so an operator stops typing URLs.
        var endpoint = await ResolveEndpointAsync(body, engagementId, listeners, cancellationToken);
        if (endpoint.Error is { } refusal)
            return (null, refusal);
        if (endpoint.Value is { } dialable && !IsDialableEndpoint(dialable))
            return (null,
                $"Endpoint must be a schemed dial the implant can serve -- http(s)://, tcp://, dns://, or doh:// -- got '{dialable}'.");
        if (body.FallbackEndpoints is { Count: > 0 } fallbacks)
        {
            foreach (var fallback in fallbacks)
            {
                if (string.IsNullOrWhiteSpace(fallback))
                    continue;
                if (!IsDialableEndpoint(fallback))
                    return (null,
                        $"Each fallback endpoint must be a schemed dial (http(s)://, tcp://, dns://, doh://), got '{fallback}'.");
            }
        }

        // The contact mode rides the beacon profile into the artifact: stream
        // (persistent, interactive) or poll (low-and-slow contacts). A typo
        // must not silently build the interactive shape for an operator who
        // asked for low-and-slow, so anything else is a 400.
        var mode = body.Mode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode))
            mode = "stream";
        if (mode is not ("stream" or "poll"))
            return (null, "Mode must be 'stream' or 'poll'.");

        // The contact the baked artifact runs: named, the mTLS socket the
        // gRPC stream dials; derived, whatever the enroll front implies -- an
        // http(s) front carries the envelope POST cycle on its own port (the
        // mainstream single-port shape), an mTLS front the stream on the same
        // socket.
        var beacon = await ResolveBeaconAsync(body, @class, mode, endpoint.Transport, endpoint.Value, engagementId, listeners, cancellationToken);
        if (beacon.Error is { } beaconRefusal)
            return (null, beaconRefusal);

        // The baked token's scope rides the same request: how many implants
        // the artifact's credential may enroll (0 = unlimited), and how long
        // the mint stays redeemable. Absent values default at mint time
        // (single use, the artifact's kill window).
        if (body.TokenMaxUses is < 0 or > 10_000)
            return (null, "TokenMaxUses must be between 0 (unlimited) and 10000.");
        if (body.TokenLifetimeSeconds is < 60 or > 2_592_000)
            return (null, "TokenLifetimeSeconds must be between 60 and 2592000 (30 days).");

        // The kill date is the artifact's optional time fuse. A pinned date in
        // the past can only be a mistake -- the artifact would refuse to run
        // the moment it landed -- so it is refused here rather than silently
        // baked; unset means open-ended (no fuse).
        if (body.KillDate is { } pinned && pinned <= DateTimeOffset.UtcNow)
            return (null, "KillDate must be in the future; leave it empty for an open-ended artifact.");

        // The stager class retired with the .NET trees, so no build carries a
        // stage-2 reference anymore; a request naming one is a leftover from
        // the retired flow and is refused with the current delivery answer.
        if (body.Stage2PayloadId is not null)
        {
            return (null,
                "stage2PayloadId rides the retired stager class; deliver the stage-2 through the launcher one-liners.");
        }
        Stage2Payload? stage2 = null;

        return (new BuildRequest(
            engagementId,
            requestedBy,
            language,
            @class,
            new TargetProfile(body.TargetOs ?? "linux", body.TargetArch ?? "amd64"),
            BuildTransport(body, endpoint.Value, beacon.Value, ExportCaPem(ca)),
            ParseDuration(body.SleepSeconds, DefaultSleep),
            ParseDuration(body.JitterSeconds, DefaultJitter),
            body.KillDate,
            mode,
            stage2,
            Format: format), null);
    }

    // Exports the teamserver CA as the PEM the artifact pins: the implant's
    // enroll client validates the server it dials against this anchor (the
    // dev CA is self-signed and in no system store; a production CA is
    // known only to this teamserver).
    private static string ExportCaPem(IImplantCertificateAuthority ca)
    {
        var der = ca.GetCaCertificate().Export(
            System.Security.Cryptography.X509Certificates.X509ContentType.Cert);
        return "-----BEGIN CERTIFICATE-----\n"
            + Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END CERTIFICATE-----\n";
    }

    // Resolves the endpoint the baked artifact dials: the listener's public
    // endpoint when the request names one (refusing anything that is not this
    // engagement's own HTTP-shaped listener), the typed endpoint
    // otherwise. The refusal is returned as a string; the value is null only when the
    // error is set. Transport reports the named listener's transport (null
    // for a typed endpoint) -- the fact the beacon resolution below needs, so
    // a derived contact matches the front it rides: an mTLS front carries
    // the gRPC stream, a web front the envelope POST cycle.
    private static async Task<(string? Value, string? Transport, string? Error)> ResolveEndpointAsync(
        Endpoints.PayloadEndpoints.BuildPayloadRequest body,
        EngagementId engagementId,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        if (body.ListenerId is not { } listenerIdText)
        {
            var typed = body.Endpoint?.Trim();
            return (typed, null, null);
        }

        if (body.Endpoint is not null)
            return (null, null, "Name either listenerId or endpoint, not both.");
        if (!Guid.TryParse(listenerIdText, out var listenerValue))
            return (null, null, "ListenerId is not a valid identifier.");

        var listener = await listeners.FindAsync(new ListenerId(listenerValue), cancellationToken);
        if (listener is null)
            return (null, null, "ListenerId does not name a listener.");
        if (listener.EngagementId is null)
            return (null, null,
                "ListenerId names a shared-tier listener; an implant dials its own engagement's listener.");
        if (listener.EngagementId != engagementId)
            return (null, null, "ListenerId names another engagement's listener.");

        // The socket family's enroll arm (Sec 8, enrollment over the stream
        // contact): a tcp listener is enroll-nameable -- the opening exchange
        // on the socket carries the EnrollRequest frames -- and the baked
        // endpoint is the transport's own dial: the host:port under tcp://.
        if (listener.Transport == "tcp")
            return SocketDial(listener.PublicEndpoint);

        // The DNS family's enroll arm (Sec 8, enrollment over DNS -- the
        // full-independence step for a DNS-only target): the enroll body
        // uploads as chunked TXT queries and the answer chunks back down, so
        // a dns or doh listener is enroll-nameable with the carrier pairing
        // rules the beacon arm applies -- a wildcard bind names no resolver
        // an implant can dial.
        if (listener.Transport is "dns" or "doh")
        {
            var dial = DnsDial(listener);
            if (dial.Error is { } dnsError)
                return (null, null, dnsError);
            return (dial.Dial, listener.Transport, null);
        }

        if (TransportProviders.Find(listener.Transport) is not KestrelEndpointProvider)
            return (null, null,
                $"The {listener.Transport} transport does not serve enrollment; build against an HTTP-shaped listener.");

        // The public endpoint may be the bare host:port redirector shape; the
        // listener's transport names the scheme the implant dials.
        var publicEndpoint = listener.PublicEndpoint.Trim();
        if (Uri.TryCreate(publicEndpoint, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return (publicEndpoint, listener.Transport, null);
        var scheme = TransportProviders.Find(listener.Transport)?.PublicEndpointScheme ?? "https";
        return ($"{scheme}://{publicEndpoint}", listener.Transport, null);
    }

    // Resolves the contact the baked artifact runs. A named beacon listener
    // or a typed beacon endpoint names the web front the WebSocket beacon
    // dials, hanging off the schemed front itself. With neither named the
    // contact derives from the enroll front: every web front (http, https,
    // or a typed http(s) URL) carries the envelope POST cycle on its own
    // port -- the mainstream single-port shape, no split required, and the
    // baked mode picks the client. Stagers never contact, so beacon fields
    // are refused on their builds.
    private static async Task<(string? Value, string? Error)> ResolveBeaconAsync(
        Endpoints.PayloadEndpoints.BuildPayloadRequest body,
        ImplantClass @class,
        string mode,
        string? enrollTransport,
        string? enrollEndpoint,
        EngagementId engagementId,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        if (body.BeaconListenerId is not null && body.BeaconEndpoint is not null)
            return (null, "Name either beaconListenerId or beaconEndpoint, not both.");
        if (@class == ImplantClass.Stager)
            return (body.BeaconListenerId is not null || body.BeaconEndpoint is not null
                ? (null, "A stager fetches its stage-2 and never contacts; beacon fields are not valid on a stager build.")
                : (null, (string?)null));

        if (body.BeaconListenerId is { } beaconListenerText)
        {
            if (!Guid.TryParse(beaconListenerText, out var beaconListenerValue))
                return (null, "BeaconListenerId is not a valid identifier.");
            var listener = await listeners.FindAsync(new ListenerId(beaconListenerValue), cancellationToken);
            if (listener is null)
                return (null, "BeaconListenerId does not name a listener.");
            if (listener.EngagementId is null)
                return (null, "BeaconListenerId names a shared-tier listener; an implant dials its own engagement's listener.");
            if (listener.EngagementId != engagementId)
                return (null, "BeaconListenerId names another engagement's listener.");
            var beaconProvider = TransportProviders.Find(listener.Transport);
            // The DNS family's carriers (architecture.md Sec 8): no native
            // channel, a TXT poll cycle over raw UDP or RFC 8484 HTTPS --
            // the beacon names the listener's own bind as the resolver plus
            // its zone, the dial shape the implant's DNS client parses. A
            // wildcard bind names no dialable resolver, so it is refused
            // with the fix rather than baked as one.
            if (listener.Transport is "dns" or "doh")
            {
                if (mode != "poll")
                    return (null,
                        $"The {listener.Transport} carrier is one-answer-one-poll; build it mode 'poll' "
                        + "(the interactive verbs ride the polls store-and-forward), or name a web front for a live stream.");
                var dnsDial = DnsDial(listener);
                if (dnsDial.Error is { } dnsError)
                    return (null, dnsError);
                return (dnsDial.Dial, null);
            }
            // The socket family's beacon arm (Sec 8): the raw-TCP listener
            // serves both shapes -- one connection is one poll contact on a
            // poll-mode bake, and a stream-mode bake holds the live session
            // the handshake's live advertisement opens -- so either mode may
            // name one and the baked beacon is the transport's own dial
            // either way (the baked mode picks the client that dials it).
            if (listener.Transport == "tcp")
            {
                var (dial, _, dialError) = SocketDial(listener.PublicEndpoint);
                return (dial, dialError);
            }
            if (beaconProvider?.ServesNativeChannel != true)
                return (null,
                    $"The beacon is a live stream and the {listener.Transport} listener carries none; name a web listener.");
            // The baked beacon URL's shape is the client the artifact dials:
            // the web family's WebSocket beacon hangs off the schemed front
            // itself.
            return (listener.PublicEndpoint, null);
        }

        if (body.BeaconEndpoint is { } beaconEndpoint)
        {
            var trimmed = beaconEndpoint.Trim();
            // The DNS family's manual dials: a resolver and a zone
            // (dns://resolver[:port]/zone, doh://resolver[:port]/zone), or
            // a bare zone (dns://zone) for the system resolver -- the
            // shapes the implant's DNS client parses. Anything else is the
            // mTLS socket's https.
            if (trimmed.StartsWith("dns://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("doh://", StringComparison.OrdinalIgnoreCase))
            {
                if (mode != "poll")
                    return (null,
                        $"The {trimmed[..trimmed.IndexOf("://", StringComparison.Ordinal)]} carrier is one-answer-one-poll; build it mode 'poll' "
                        + "(the interactive verbs ride the polls store-and-forward), or name a web front for a live stream.");
                var rest = trimmed[(trimmed.IndexOf("://", StringComparison.Ordinal) + 3)..];
                if (rest.Length == 0)
                    return (null,
                        $"A dns/doh beacon endpoint names a resolver and a zone (dns://resolver:53/zone, doh://resolver:443/zone) or a bare zone (dns://zone), got '{beaconEndpoint}'.");
                return (trimmed, null);
            }
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return (null,
                    $"Beacon endpoint must be an absolute https URL naming the web front the WebSocket beacon dials, or a dns:// dial, got '{beaconEndpoint}'.");
            return (trimmed, null);
        }

        // The derived single-front bake never names the beacon: a web front's
        // native carrier is the WebSocket beacon hanging off the schemed
        // front, which the single-port shape already dials without a split --
        // the beacon stays unnamed and the baked mode picks the client. The
        // socket family bakes the same dial
        // under either mode (the client the mode picks holds the session or
        // cycles the connection), so no gate applies to it here. The DNS
        // family stays poll-only: a datagram poll has no stream to hold,
        // named listener or typed scheme alike.
        if (TypedPollOnlyScheme(enrollEndpoint) is { } typedCarrier && mode != "poll")
            return (null,
                $"The {typedCarrier} carrier is one-answer-one-poll; build it mode 'poll' "
                + "(the interactive verbs ride the polls store-and-forward), or name a web front for a live stream.");
        if (enrollTransport is "dns" or "doh" && mode != "poll")
            return (null,
                $"The {enrollTransport} carrier is one-answer-one-poll; build it mode 'poll' "
                + "(the interactive verbs ride the polls store-and-forward), or name a web front for a live stream.");
        return (null, (string?)null);
    }


    // Builds the malleable transport profile off the request body
    // (architecture.md Sec 7). Endpoint and uri path are the always-set
    // positional fields; the malleable knobs default when the operator omits
    // them, so a minimal build request stays valid. Headers arrive as a flat
    // name/value map and are applied verbatim; an empty or null map adds none.
    private static TransportProfile BuildTransport(
        Endpoints.PayloadEndpoints.BuildPayloadRequest body,
        string? endpoint,
        string? beaconEndpoint,
        string caPem)
    {
        var profile = new TransportProfile(
            endpoint ?? "http://localhost:5080",
            body.UriPath ?? "/beacon")
        {
            // The split-socket shape: enroll dials one host, the beacon
            // stream another. Null keeps the derived single-front bake.
            BeaconEndpoint = beaconEndpoint,
            // The pinned teamserver CA rides every build.
            CaPem = caPem,
        };

        if (!string.IsNullOrWhiteSpace(body.EnrollPath))
            profile = profile with { EnrollPath = body.EnrollPath };
        if (!string.IsNullOrWhiteSpace(body.UserAgent))
            profile = profile with { UserAgent = body.UserAgent };
        if (body.Headers is { Count: > 0 } headers)
            profile = profile with { Headers = headers };
        if (body.RequestTimeoutSeconds is { } timeoutSeconds and >= 0)
            profile = profile with { RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds) };
        if (body.Envelope is { } envelope
            && Enum.TryParse<TransportEnvelope>(envelope, ignoreCase: true, out var parsed))
        {
            profile = profile with { Envelope = parsed };
        }
        // Contact protection is its own knob, independent of the enroll-body
        // envelope (architecture.md Sec 8/9): on by default, and only an
        // explicit opt-out rides -- the lab-debug plaintext frame.
        if (body.ContactProtection is false)
            profile = profile with { ContactProtection = false };
        if (body.FallbackEndpoints is { Count: > 0 } fallbacks)
        {
            // The fallback list is the egress walk order (architecture.md Sec 8):
            // blanks are dropped rather than rejected so a trailing separator in
            // an operator's list is not a 400, and the surviving order is baked
            // verbatim.
            var cleaned = fallbacks
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .ToArray();
            if (cleaned.Length > 0)
                profile = profile with { FallbackEndpoints = cleaned };
        }

        return profile;
    }

    // Case-insensitive enum parse off the request string, with a fallback when
    // the field is absent so a minimal request stays valid.
    private static bool TryParseLanguage(string? text, out Language language)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            language = Language.Rust; // the in-tree reference unit is Rust (Sec 12.2).
            return true;
        }
        return Enum.TryParse(text, ignoreCase: true, out language);
    }

    private static bool TryParseClass(string? text, out ImplantClass @class)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            @class = ImplantClass.Stage2;
            return true;
        }
        return Enum.TryParse(text, ignoreCase: true, out @class);
    }

    private static TimeSpan ParseDuration(double? seconds, TimeSpan fallback)
        => seconds is { } value && value >= 0
            ? TimeSpan.FromSeconds(Math.Min(value, MaxDurationSeconds))
            : fallback;

    // An endpoint the implant can dial: an absolute http(s) URL, or the
    // socket or DNS family's dial (architecture.md Sec 8 -- a tcp-, dns-,
    // or doh-schemed enroll endpoint runs the frame or chunk
    // exchange its module dials, and the egress walk treats every entry as
    // a URL). A bare host or a typo'd scheme strands the payload on target.
    private static bool IsDialableEndpoint(string text)
        => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp
                || uri.Scheme == Uri.UriSchemeHttps
                || uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("dns", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("doh", StringComparison.OrdinalIgnoreCase));

    // The DNS carrier a typed endpoint's scheme names -- the typed-endpoint
    // twin of the named-listener mode gate, so a stream-mode build cannot
    // bake a datagram poll just because it was typed instead of picked. The
    // socket schemes are absent: both their shapes bake.
    private static string? TypedPollOnlyScheme(string? endpoint)
    {
        var trimmed = endpoint?.Trim();
        if (trimmed is null || trimmed.Length < 7)
            return null;
        foreach (var scheme in new[] { "dns://", "doh://" })
        {
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return scheme[..^3];
        }
        return null;
    }

    // The DNS family's baked dial (Sec 8), shared by the enroll and beacon
    // arms: the listener's own bind as the resolver plus its zone, the dial
    // shape the implant's DNS client parses. A wildcard bind names no
    // dialable resolver, so it is refused with the fix rather than baked as
    // one.
    private static (string? Dial, string? Error) DnsDial(Rod.Transport.Listeners.Listener listener)
    {
        var scheme = listener.Transport == "doh" ? "doh" : "dns";
        var zone = listener.PublicEndpoint.Trim().TrimEnd('.').ToLowerInvariant();
        var bind = listener.BindAddress.Trim();
        if (bind.StartsWith("0.0.0.0:") || bind.StartsWith("[::]:") || bind.StartsWith(":::"))
            return (null,
                $"A wildcard-bound {scheme} listener names no resolver an implant can dial; bind it "
                + "to a concrete interface, or type the dial manually under Advanced "
                + $"({scheme}://resolver:{(scheme == "doh" ? "443" : "53")}/{(zone.Length > 0 ? zone : "zone")}).");
        return ($"{scheme}://{bind}/{zone}", null);
    }

    // The socket family's baked dial (Sec 8, enrollment over the stream
    // contact): the raw-TCP listener's host:port public endpoint completes
    // under tcp://.
    private static (string? Value, string? Transport, string? Error) SocketDial(string publicEndpoint)
    {
        var trimmed = publicEndpoint.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var dial) && dial.Scheme == "tcp")
            return (trimmed, "tcp", null);
        return ($"tcp://{trimmed}", "tcp", null);
    }
}
