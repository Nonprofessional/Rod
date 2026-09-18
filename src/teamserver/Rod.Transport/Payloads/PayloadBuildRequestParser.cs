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
        IPayloadStore payloads,
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

        // The in-tree .NET toolchain bundles a runtime for every pair it maps
        // except x86 off Windows (no linux-x86/osx-x86 runtime exists), so the
        // pair is refused here with the reason instead of failing the queued
        // job at restore with the toolchain's own error.
        if (language == Language.DotNet
            && string.Equals(body.TargetArch ?? "amd64", "x86", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(body.TargetOs ?? "linux", "windows", StringComparison.OrdinalIgnoreCase))
        {
            return (null,
                "The .NET toolchain builds x86 artifacts only for Windows targets; choose amd64 or arm64.");
        }

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
                $"Endpoint must be an absolute http(s) or quic URL the implant can dial, got '{dialable}'.");
        if (body.FallbackEndpoints is { Count: > 0 } fallbacks)
        {
            foreach (var fallback in fallbacks)
            {
                if (string.IsNullOrWhiteSpace(fallback))
                    continue;
                if (!IsDialableEndpoint(fallback))
                    return (null,
                        $"Each fallback endpoint must be an absolute http(s) or quic URL, got '{fallback}'.");
            }
        }

        // The check-in mode rides the beacon profile into the artifact: stream
        // (persistent, interactive) or poll (low-and-slow check-ins). A typo
        // must not silently build the interactive shape for an operator who
        // asked for low-and-slow, so anything else is a 400.
        var mode = body.Mode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode))
            mode = "stream";
        if (mode is not ("stream" or "poll"))
            return (null, "Mode must be 'stream' or 'poll'.");

        // The check-in the baked artifact runs: named, the mTLS socket the
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

        // The stager output class (architecture.md Sec 6) references the
        // stage-2 payload it fetches at run time: resolve it here so the build
        // contract carries a verified reference -- the payload's id and
        // fingerprint -- rather than a raw operator string.
        Stage2Payload? stage2 = null;
        if (@class == ImplantClass.Stager)
        {
            if (body.Stage2PayloadId is not { } stage2Id)
                return (null,
                    "A stager build requires stage2PayloadId: the built stage-2 payload the stager fetches.");
            if (!Guid.TryParse(stage2Id, out var stage2Value))
                return (null, "Stage2PayloadId is not a valid identifier.");
            var payload = await payloads.FindAsync(stage2Value, engagementId.Value, cancellationToken);
            if (payload is null)
                return (null,
                    "Stage2PayloadId does not name a payload in this engagement; build the stage-2 first.");
            stage2 = new Stage2Payload(stage2Value, payload.Fingerprint);
        }
        else if (body.Stage2PayloadId is not null)
        {
            return (null, "stage2PayloadId is only valid on a stager-class build.");
        }

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
            stage2), null);
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
    // engagement's own HTTP-shaped or quic listener), the typed endpoint
    // otherwise. The refusal is returned as a string; the value is null only when the
    // error is set. Transport reports the named listener's transport (null
    // for a typed endpoint) -- the fact the beacon resolution below needs, so
    // a derived check-in matches the front it rides: an mTLS front carries
    // the gRPC stream, a web front the envelope POST cycle, a quic front
    // its own session dial.
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

        // The quic listener is enroll-nameable (architecture.md Sec 8,
        // enrollment over QUIC): its opening stream carries the enroll
        // exchange the web route's JSON body also carries, so a build may
        // name it and the baked enroll endpoint is the transport's own dial
        // -- the same scheme completion the quic beacon arm applies.
        if (listener.Transport == "quic")
        {
            var quicEnroll = listener.PublicEndpoint.Trim();
            if (Uri.TryCreate(quicEnroll, UriKind.Absolute, out var quicDial) && quicDial.Scheme == "quic")
                return (quicEnroll, listener.Transport, null);
            return ($"quic://{quicEnroll}", listener.Transport, null);
        }

        // The socket family's enroll arm (Sec 8, enrollment over the stream
        // check-in): an smb or tcp listener is enroll-nameable the same way
        // -- the opening exchange on the pipe or socket carries the
        // EnrollRequest frames -- and the baked endpoint is the transport's
        // own dial: the pipe path in URL form, the host:port under tcp://.
        if (listener.Transport is "smb" or "tcp")
            return SocketDial(listener.Transport, listener.PublicEndpoint);

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
                $"The {listener.Transport} transport does not serve enrollment; build against an HTTP-shaped listener or a quic listener.");

        // The public endpoint may be the bare host:port redirector shape; the
        // listener's transport names the scheme the implant dials.
        var publicEndpoint = listener.PublicEndpoint.Trim();
        if (Uri.TryCreate(publicEndpoint, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return (publicEndpoint, listener.Transport, null);
        var scheme = TransportProviders.Find(listener.Transport)?.PublicEndpointScheme ?? "https";
        return ($"{scheme}://{publicEndpoint}", listener.Transport, null);
    }

    // Resolves the check-in the baked artifact runs. A named beacon listener
    // or a typed beacon endpoint names the mTLS socket the gRPC stream dials,
    // and bakes as the bare authority -- to the implant a schemed beacon URL
    // means the envelope POST cycle, so the stream's dial shape carries no
    // scheme. With neither named the check-in derives from the enroll front:
    // an mTLS front carries the gRPC stream on the same socket, every web
    // front (http, https, or a typed http(s) URL) the envelope POST cycle on
    // its own port -- the mainstream single-port shape, no split required.
    // A socket-owning native dial (the QUIC stream) completes its bare
    // public endpoint with the transport's own scheme, the URL shape the
    // artifact's check-in client picks by. Stagers never check in, so beacon
    // fields are refused on their builds.
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
                ? (null, "A stager fetches its stage-2 and never checks in; beacon fields are not valid on a stager build.")
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
                        + "(the interactive verbs ride the polls store-and-forward), or name a web, mTLS, or QUIC front for a live stream.");
                var dnsDial = DnsDial(listener);
                if (dnsDial.Error is { } dnsError)
                    return (null, dnsError);
                return (dnsDial.Dial, null);
            }
            // The socket family's beacon arm (Sec 8): the named-pipe and
            // raw-TCP listeners are poll-only carriers -- one connection is
            // one check-in, the interactive verbs riding the cycles
            // store-and-forward -- so a poll-mode build may name one and the
            // baked beacon is the transport's own dial. A stream-mode naming
            // is the incoherent pair (no live stream exists to hold),
            // refused with the fix.
            if (listener.Transport is "smb" or "tcp")
            {
                if (mode != "poll")
                    return (null,
                        $"The {listener.Transport} carrier is one-connection-one-check-in; build it mode 'poll' "
                        + "(the interactive verbs ride the cycles store-and-forward), or name a web, mTLS, or QUIC front for a live stream.");
                var (dial, _, dialError) = SocketDial(listener.Transport, listener.PublicEndpoint);
                return (dial, dialError);
            }
            if (beaconProvider?.ServesNativeChannel != true)
                return (null,
                    $"The beacon is a live stream and the {listener.Transport} listener carries none; name the mTLS listener or a web listener.");
            // The baked beacon URL's shape is the client the artifact dials: the
            // mTLS shape's gRPC stream dials the bare authority, the web family's
            // WebSocket beacon hangs off the schemed front itself.
            if (beaconProvider is KestrelEndpointProvider { Posture: ListenerTlsPosture mutual } && mutual == ListenerTlsPosture.MutualAsk)
                return (BeaconAuthority(listener.PublicEndpoint), null);
            if (beaconProvider is KestrelEndpointProvider)
                return (listener.PublicEndpoint, null);
            // A socket-owning native dial (the QUIC stream): the bare
            // host:port public endpoint completes with the transport's own
            // scheme -- the URL shape the artifact's check-in client picks
            // by. Either mode bakes: stream holds the session, poll ends
            // each cycle on the client's idle window at the baked cadence.
            var quicEndpoint = listener.PublicEndpoint.Trim();
            if (Uri.TryCreate(quicEndpoint, UriKind.Absolute, out var quicDial)
                && quicDial.Scheme == beaconProvider.PublicEndpointScheme)
                return (quicEndpoint, null);
            return ($"{beaconProvider.PublicEndpointScheme}://{quicEndpoint}", null);
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
                var rest = trimmed[(trimmed.IndexOf("://", StringComparison.Ordinal) + 3)..];
                if (rest.Length == 0)
                    return (null,
                        $"A dns/doh beacon endpoint names a resolver and a zone (dns://resolver:53/zone, doh://resolver:443/zone) or a bare zone (dns://zone), got '{beaconEndpoint}'.");
                return (trimmed, null);
            }
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return (null,
                    $"Beacon endpoint must be an absolute https URL naming the mTLS socket the check-in stream dials, or a dns:// dial, got '{beaconEndpoint}'.");
            return (BeaconAuthority(trimmed), null);
        }

        // The derived single-front bake names the beacon only when the front's
        // own socket IS the stream's socket -- the mTLS shape, whose gRPC
        // stream dials the bare authority. A web front's native carrier is the
        // WebSocket beacon hanging off the schemed front, which the single-port
        // shape already dials without a split: the beacon stays unnamed and the
        // baked mode picks the client. A quic front's derived beacon is its
        // own session dial (the quic-schemed enroll endpoint carries no path
        // to strip), and either mode bakes -- the client holds the session or
        // cycles it on the idle window at the baked cadence.
        // The poll-only families' derived shape: an smb, tcp, dns, or doh
        // enroll front holds no live stream, so the walk's own mode gate
        // applies here too -- stream mode names one the carrier does not
        // hold.
        if (enrollTransport is "smb" or "tcp" && mode != "poll")
            return (null,
                $"The {enrollTransport} carrier is one-connection-one-check-in; build it mode 'poll' "
                + "(the interactive verbs ride the cycles store-and-forward), or name a web, mTLS, or QUIC front for a live stream.");
        if (enrollTransport is "dns" or "doh" && mode != "poll")
            return (null,
                $"The {enrollTransport} carrier is one-answer-one-poll; build it mode 'poll' "
                + "(the interactive verbs ride the polls store-and-forward), or name a web, mTLS, or QUIC front for a live stream.");
        if (enrollTransport is not null
            && TransportProviders.Find(enrollTransport) is KestrelEndpointProvider { Posture: ListenerTlsPosture frontMutual }
            && frontMutual == ListenerTlsPosture.MutualAsk
            && enrollEndpoint is { } front)
            return (BeaconAuthority(front), null);
        return (null, (string?)null);
    }

    // Strips a TLS endpoint down to the authority the gRPC stream dials. The
    // listener record normalizes its TLS public endpoints to https URLs and a
    // typed beacon endpoint arrives as one; the baked mTLS dial shape must
    // carry none, because to the implant a schemed beacon URL is the envelope
    // POST cycle's.
    private static string BeaconAuthority(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            return uri.Authority;
        return trimmed;
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
        // Check-in protection is its own knob, independent of the enroll-body
        // envelope (architecture.md Sec 8/9): on by default, and only an
        // explicit opt-out rides -- the lab-debug plaintext frame.
        if (body.CheckInProtection is false)
            profile = profile with { CheckInProtection = false };
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
            language = Language.DotNet; // the in-tree reference unit is .NET (ADR 0009).
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

    // An endpoint the implant can dial: an absolute http(s) URL, or the QUIC,
    // socket, or DNS family's dial (architecture.md Sec 8 -- a quic-, tcp-,
    // smb-, dns-, or doh-schemed enroll endpoint runs the frame or chunk
    // exchange its module dials, and the egress walk treats every entry as a
    // URL). A bare host or a typo'd scheme strands the payload on target.
    private static bool IsDialableEndpoint(string text)
        => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp
                || uri.Scheme == Uri.UriSchemeHttps
                || uri.Scheme.Equals("quic", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("smb", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("dns", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("doh", StringComparison.OrdinalIgnoreCase));

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
    // check-in): the raw-TCP listener's host:port public endpoint completes
    // under tcp://, and the smb listener's pipe path (\\host\pipe\name)
    // becomes the URL form smb://host/pipe/name -- a dot host naming the
    // local machine.
    private static (string? Value, string? Transport, string? Error) SocketDial(string transport, string publicEndpoint)
    {
        var trimmed = publicEndpoint.Trim();
        if (transport == "tcp")
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var dial) && dial.Scheme == "tcp")
                return (trimmed, transport, null);
            return ($"tcp://{trimmed}", transport, null);
        }

        var parts = trimmed.TrimStart('\\').Split('\\');
        if (parts.Length < 3 || !string.Equals(parts[1], "pipe", StringComparison.OrdinalIgnoreCase))
            return (null, transport,
                $"The smb listener's public endpoint must be the pipe path implants dial (\\\\host\\pipe\\name), got '{publicEndpoint}'.");
        var pipeName = string.Join('/', parts.Skip(2));
        return ($"smb://{parts[0]}/pipe/{pipeName}", transport, null);
    }
}
