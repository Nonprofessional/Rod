using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
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
    /// its public endpoint the same way.
    /// </summary>
    public static async Task<(BuildRequest? Request, string? Error)> ParseAsync(
        Endpoints.PayloadEndpoints.BuildPayloadRequest body,
        EngagementId engagementId,
        OperatorId requestedBy,
        IPayloadStore payloads,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        // Language and class come in as strings and parse to the enums; anything
        // that does not parse is a 400. The defaults keep a minimal request
        // valid (the in-tree .NET unit, a stage-2 implant, linux/amd64).
        if (!TryParseLanguage(body.Language, out var language))
            return (null, "Language is not recognized.");
        if (!TryParseClass(body.Class, out var @class))
            return (null, "Implant class is not recognized.");

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

        // The check-in mode rides the beacon profile into the artifact: stream
        // (persistent, interactive) or poll (low-and-slow check-ins). A typo
        // must not silently build the interactive shape for an operator who
        // asked for low-and-slow, so anything else is a 400.
        var mode = body.Mode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode))
            mode = "stream";
        if (mode is not ("stream" or "poll"))
            return (null, "Mode must be 'stream' or 'poll'.");

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
            BuildTransport(body, endpoint.Value),
            ParseDuration(body.SleepSeconds, DefaultSleep),
            ParseDuration(body.JitterSeconds, DefaultJitter),
            body.KillDate,
            mode,
            stage2), null);
    }

    // Resolves the endpoint the baked artifact dials: the listener's public
    // endpoint when the request names one (refusing anything that is not this
    // engagement's own HTTP-shaped listener), the typed endpoint otherwise.
    // The refusal is returned as a string; the value is null only when the
    // error is set.
    private static async Task<(string? Value, string? Error)> ResolveEndpointAsync(
        Endpoints.PayloadEndpoints.BuildPayloadRequest body,
        EngagementId engagementId,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        if (body.ListenerId is not { } listenerIdText)
            return (body.Endpoint, null);

        if (body.Endpoint is not null)
            return (null, "Name either listenerId or endpoint, not both.");
        if (!Guid.TryParse(listenerIdText, out var listenerValue))
            return (null, "ListenerId is not a valid identifier.");

        var listener = await listeners.FindAsync(new ListenerId(listenerValue), cancellationToken);
        if (listener is null)
            return (null, "ListenerId does not name a listener.");
        if (listener.EngagementId is null)
            return (null,
                "ListenerId names a shared-tier listener; an implant dials its own engagement's listener.");
        if (listener.EngagementId != engagementId)
            return (null, "ListenerId names another engagement's listener.");
        if (listener.Transport is not (ListenerTransport.Http or ListenerTransport.Mtls or ListenerTransport.HttpsEnvelope))
            return (null,
                $"The {listener.Transport.ToString().ToLowerInvariant()} transport does not serve http(s) enrollment; build against an HTTP-shaped listener.");

        // The public endpoint may be the bare host:port redirector shape; the
        // listener's transport names the scheme the implant dials.
        var publicEndpoint = listener.PublicEndpoint.Trim();
        if (Uri.TryCreate(publicEndpoint, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return (publicEndpoint, null);
        var scheme = listener.Transport == ListenerTransport.Http ? "http" : "https";
        return ($"{scheme}://{publicEndpoint}", null);
    }

    // Builds the malleable transport profile off the request body
    // (architecture.md Sec 7). Endpoint and uri path are the always-set
    // positional fields; the malleable knobs default when the operator omits
    // them, so a minimal build request stays valid. Headers arrive as a flat
    // name/value map and are applied verbatim; an empty or null map adds none.
    private static TransportProfile BuildTransport(
        Endpoints.PayloadEndpoints.BuildPayloadRequest body,
        string? endpoint)
    {
        var profile = new TransportProfile(
            endpoint ?? "http://localhost:5080",
            body.UriPath ?? "/beacon");

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
