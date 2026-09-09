using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Pki;
using Rod.Transport.Listeners;

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
                $"Endpoint must be an absolute http(s) URL the implant can dial, got '{dialable}'.");
        if (body.FallbackEndpoints is { Count: > 0 } fallbacks)
        {
            foreach (var fallback in fallbacks)
            {
                if (string.IsNullOrWhiteSpace(fallback))
                    continue;
                if (!IsDialableEndpoint(fallback))
                    return (null,
                        $"Each fallback endpoint must be an absolute http(s) URL, got '{fallback}'.");
            }
        }

        // The check-in the baked artifact runs: named, the mTLS socket the
        // gRPC stream dials; derived, whatever the enroll front implies -- an
        // http(s) front carries the envelope POST cycle on its own port (the
        // mainstream single-port shape), an mTLS front the stream on the same
        // socket.
        var beacon = await ResolveBeaconAsync(body, @class, endpoint.Transport, endpoint.Value, engagementId, listeners, cancellationToken);
        if (beacon.Error is { } beaconRefusal)
            return (null, beaconRefusal);

        // The check-in mode rides the beacon profile into the artifact: stream
        // (persistent, interactive) or poll (low-and-slow check-ins). A typo
        // must not silently build the interactive shape for an operator who
        // asked for low-and-slow, so anything else is a 400.
        var mode = body.Mode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode))
            mode = "stream";
        if (mode is not ("stream" or "poll"))
            return (null, "Mode must be 'stream' or 'poll'.");

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
    // engagement's own HTTP-shaped listener), the typed endpoint otherwise.
    // The refusal is returned as a string; the value is null only when the
    // error is set. Transport reports the named listener's transport (null
    // for a typed endpoint) -- the fact the beacon resolution below needs, so
    // a derived check-in matches the front it rides: an mTLS front carries
    // the gRPC stream, a web front the envelope POST cycle.
    private static async Task<(string? Value, ListenerTransport? Transport, string? Error)> ResolveEndpointAsync(
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
        if (listener.Transport is not (ListenerTransport.Http or ListenerTransport.Https
            or ListenerTransport.Mtls))
            return (null, null,
                $"The {listener.Transport.ToString().ToLowerInvariant()} transport does not serve http(s) enrollment; build against an HTTP-shaped listener.");

        // The public endpoint may be the bare host:port redirector shape; the
        // listener's transport names the scheme the implant dials.
        var publicEndpoint = listener.PublicEndpoint.Trim();
        if (Uri.TryCreate(publicEndpoint, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return (publicEndpoint, listener.Transport, null);
        var scheme = listener.Transport == ListenerTransport.Http ? "http" : "https";
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
    // Stagers never check in, so beacon fields are refused on their builds.
    private static async Task<(string? Value, string? Error)> ResolveBeaconAsync(
        Endpoints.PayloadEndpoints.BuildPayloadRequest body,
        ImplantClass @class,
        ListenerTransport? enrollTransport,
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
            if (listener.Transport != ListenerTransport.Mtls)
                return (null,
                    $"The beacon is the gRPC stream over mTLS; the {listener.Transport.WireName()} listener cannot carry it. Name the mTLS listener.");

            return (BeaconAuthority(listener.PublicEndpoint), null);
        }

        if (body.BeaconEndpoint is { } beaconEndpoint)
        {
            var trimmed = beaconEndpoint.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return (null,
                    $"Beacon endpoint must be an absolute https URL naming the mTLS socket the check-in stream dials, got '{beaconEndpoint}'.");
            return (BeaconAuthority(trimmed), null);
        }

        if (enrollTransport == ListenerTransport.Mtls && enrollEndpoint is { } front)
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

    // An endpoint the implant can dial: an absolute http(s) URL. The implant's
    // egress walk treats every entry as a URL (enroll over the scheme, beacon
    // host from the authority), so a bare host or a typo'd scheme strands the
    // payload on target.
    private static bool IsDialableEndpoint(string text)
        => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
