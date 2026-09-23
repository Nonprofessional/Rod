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
    /// string naming the first refusal. Every cross-reference resolves
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
        Rod.Audit.IPayloadStore payloads,
        CancellationToken cancellationToken)
    {
        // Language and class come in as strings and parse to the enums; anything
        // that does not parse is a 400. The defaults keep a minimal request
        // valid (the in-tree Rust unit, an Implant-class build, linux/amd64).
        if (!TryParseLanguage(body.Language, out var language))
            return (null, "Language is not recognized.");
        // The stager class is retired with the .NET trees: delivery rides the
        // launcher one-liners (the disk families plus the in-memory memfd
        // family), which fetch the payload over the same token-gated route a
        // loader ever used. The refusal names the retired spelling before the
        // enum parse, which no longer knows it.
        if (string.Equals(body.Class?.Trim(), "stager", StringComparison.OrdinalIgnoreCase))
            return (null,
                "The stager class is retired; deliver the payload through the launcher one-liners (launchers render them per payload).");
        if (!TryParseClass(body.Class, out var @class))
            return (null, "Implant class is not recognized.");
        // The kind names the delivery tier: the implant (the default, the
        // full product) or the loader that fetches and runs a payload
        // from memory. The loader gates live below, after the endpoint
        // resolves -- they read the front the bake dials.
        if (!PayloadKinds.TryParse(body.Kind, out var kind))
            return (null, "Kind must be 'implant' (the default) or 'loader'.");
        // The format axis carries the deployment shapes (architecture.md
        // Sec 6). The in-tree Rust unit produces the executable spellings
        // only: they are synonyms over the same native binary, and the
        // loader crate beside it emits the loader tier. The shared-library
        // and shellcode shapes are contract slots -- the parser names them
        // so a request can be refused with the delivery story, not a bare
        // parse error, until the toolchain that produces them lands.
        if (!ArtifactFormats.TryParse(body.Format, out var format))
            return (null, "Format must be one of 'exe' (the default), 'exe-trimmed', 'aot', 'dll', 'so', or 'shellcode'.");
        if (format is ArtifactFormat.Dll or ArtifactFormat.SharedObject or ArtifactFormat.Shellcode)
            return (null,
                $"The '{ArtifactFormats.Name(format)}' format is a contract slot the in-tree Rust unit does not produce yet "
                + "-- it names the loader and injection deliveries; build 'exe' or 'aot' for now.");
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
        // The TLS trust posture is the front's fact, not the request's: the
        // named listener records whose certificate it presents (the
        // engagement CA, or a real-domain chain an operator-run edge
        // terminates), and the build inherits it as the roots it bakes
        // (architecture.md Sec 9). A typed DNS dial names no listener and
        // carries no TLS posture -- pinned stands. A request that still
        // spells a posture may only agree with the front; the knob moved to
        // the listener, and a contradiction here would bake roots the front
        // cannot verify against.
        var trust = endpoint.Trust ?? "pinned";
        if (body.Trust is { } asked && !string.IsNullOrWhiteSpace(asked))
        {
            var normalized = asked.Trim().ToLowerInvariant();
            if (normalized is not ("pinned" or "public"))
                return (null, "Trust must be 'pinned' or 'public'.");
            if (!string.Equals(normalized, trust, StringComparison.OrdinalIgnoreCase))
                return (null,
                    $"The front presents a '{trust}' certificate; set the posture on the listener, not the build "
                    + "-- a build's roots follow the certificate the front actually serves.");
        }

        if (body.FallbackEndpoints is { Count: > 0 } fallbacks)
        {
            var frontFamily = SchemeFamily(endpoint.Value);
            // Loaded only when an https fallback needs its front resolved;
            // the engagement's listener count is small either way.
            IReadOnlyList<Rod.Transport.Listeners.Listener>? fronts = null;
            foreach (var fallback in fallbacks)
            {
                if (string.IsNullOrWhiteSpace(fallback))
                    continue;
                if (!IsDialableEndpoint(fallback))
                    return (null,
                        $"Each fallback endpoint must be a schemed dial (http(s)://, tcp://, dns://, doh://), got '{fallback}'.");
                if (frontFamily is { } family && SchemeFamily(fallback) != family)
                    return (null,
                        $"Each fallback endpoint must dial the front's own scheme family -- the '{endpoint.Value}' front walks {FamilyShapes(family)} fallbacks only, got '{fallback}'.");
                // An https fallback walks under the same roots as the front:
                // the artifact bakes one root set, so a fallback presenting
                // the other certificate posture is a dead entry the family
                // check alone would pass. Only a dial that names one of this
                // engagement's listeners can be checked -- a typed address
                // (a redirector this teamserver cannot see) stays the
                // operator's call.
                if (frontFamily == "web"
                    && fallback.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    fronts ??= await listeners.ListAsync(cancellationToken);
                    var named = fronts.FirstOrDefault(l => NamesFront(fallback, l));
                    if (named is not null
                        && !string.Equals(named.TrustPosture, trust, StringComparison.OrdinalIgnoreCase))
                        return (null,
                            $"The fallback '{fallback}' names {named.Name}, a '{named.TrustPosture}' front, while this build rides '{trust}' roots -- pick fallbacks that share the front's certificate posture.");
                }
            }
        }

        // A public posture needs a TLS dial to ride: the primary or any
        // fallback must be https, where the front's publicly-trusted chain is
        // presented (an operator-run edge in front of the teamserver holds
        // the certificate for the real domain). The listener gate refuses
        // this pairing at creation, so reaching here means the endpoint
        // moved after the fact -- a repoint away from the https address.
        if (trust == "public"
            && endpoint.Value?.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase) != true
            && body.FallbackEndpoints?.Any(f =>
                f.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)) != true)
            return (null,
                "This front's posture is 'public' but its dial is no longer https (a repoint moved it?) -- "
                + "point the listener back at the edge's https address, or set its posture to pinned.");

        // The contact mode rides the beacon profile into the artifact: stream
        // (persistent, interactive) or poll (low-and-slow contacts). Poll is
        // the default -- every family carries it, and a held connection is a
        // standing detection signal an operator should opt into, not out of.
        // A typo must not silently build the interactive shape for an
        // operator who asked for low-and-slow, so anything else is a 400.
        var mode = body.Mode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode))
            mode = "poll";
        if (mode is not ("stream" or "poll"))
            return (null, "Mode must be 'stream' or 'poll'.");

        // The contact the baked artifact runs: named, the mTLS socket the
        // gRPC stream dials; derived, whatever the enroll front implies -- an
        // http(s) front carries the envelope POST cycle on its own port (the
        // mainstream single-port shape), an mTLS front the stream on the same
        // socket.
        var beacon = await ResolveBeaconAsync(body, @class, mode, trust, endpoint.Transport, endpoint.Value, engagementId, listeners, cancellationToken);
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

        // The loader tier's own gates. The loader is a no_std dialer
        // by design (architecture.md Sec 6, staging): it speaks plain HTTP
        // to a literal IPv4, carries no TLS and no resolver, and runs on the
        // two Linux arches its crate compiles. Every refusal names the shape
        // it wants rather than failing inside cargo with less to act on.
        if (kind == PayloadKind.Loader)
        {
            if (language != Language.Rust)
                return (null, "The loader tier is the in-tree Rust unit's artifact; set language to Rust or leave it empty.");
            if (format is not (ArtifactFormat.SingleFileExe or ArtifactFormat.TrimmedExe or ArtifactFormat.NativeAot))
                return (null, "The loader is a native executable; build it with the default 'exe' format.");
            var os = (body.TargetOs ?? "linux").Trim().ToLowerInvariant();
            var arch = (body.TargetArch ?? "amd64").Trim().ToLowerInvariant();
            if (os != "linux")
                return (null,
                    "The loader is a Linux memfd shape; a Windows target delivers through the launcher one-liners.");
            if (arch is not ("amd64" or "x64" or "x86_64" or "arm64" or "aarch64"))
                return (null, "The loader compiles for linux amd64 and arm64 only.");
            // The dial: cleartext HTTP on a literal IPv4. A hostname needs a
            // resolver and an https front needs TLS -- both implant-tier
            // machinery this tier refuses to carry. The stage rides sealed
            // under the per-build key, the same posture as the cleartext
            // contact, so the plain transport costs nothing but leaves the
            // front's spelling narrow on purpose.
            if (!endpoint.Value!.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return (null,
                    "The loader dials a cleartext http front (no TLS in the tier); pick an http listener.");
            // The stage reference: this engagement's own stored payload, and
            // not another loader -- the loader delivers the implant tier,
            // and a chain of dialers is a footprint, not a capability.
            if (body.DeliversPayloadId is not { } deliversText)
                return (null, "A loader build names the stored payload it delivers (deliversPayloadId).");
            if (!Guid.TryParse(deliversText, out var deliversValue))
                return (null, "DeliversPayloadId is not a valid identifier.");
            var delivered = await payloads.FindAsync(deliversValue, engagementId.Value, cancellationToken);
            if (delivered is null)
                return (null, "DeliversPayloadId does not name a payload in this engagement.");
            if (delivered.DeliversPayloadId is not null)
                return (null, "DeliversPayloadId names a loader; the loader delivers the implant tier, not another loader.");
            return (new BuildRequest(
                engagementId,
                requestedBy,
                language,
                @class,
                new TargetProfile(body.TargetOs ?? "linux", body.TargetArch ?? "amd64"),
                BuildTransport(body, endpoint.Value!, beacon.Value, ExportCaPem(ca),
                    trust == "public" ? TlsTrust.Public : TlsTrust.Pinned),
                ParseDuration(body.SleepSeconds, DefaultSleep),
                ParseDuration(body.JitterSeconds, DefaultJitter),
                body.KillDate,
                mode,
                Format: format,
                Kind: kind,
                DeliversPayloadId: deliversValue), null);
        }

        // The loader class retired with the .NET trees, so no build carries a
        // fetched-payload reference anymore; a request naming one is a leftover from
        // the retired flow and is refused with the current delivery answer.
        if (body.Stage2PayloadId is not null)
        {
            return (null,
                "stage2PayloadId rides the retired stager vocabulary; the staged answer is the loader tier "
                + "(kind 'loader' with deliversPayloadId), or the launcher one-liners for a plain fetch.");
        }
        return (new BuildRequest(
            engagementId,
            requestedBy,
            language,
            @class,
            new TargetProfile(body.TargetOs ?? "linux", body.TargetArch ?? "amd64"),
            // Non-null by construction: every resolution path either returns
            // a dial or an error, and the error returned above.
            BuildTransport(body, endpoint.Value!, beacon.Value, ExportCaPem(ca),
                trust == "public" ? TlsTrust.Public : TlsTrust.Pinned),
            ParseDuration(body.SleepSeconds, DefaultSleep),
            ParseDuration(body.JitterSeconds, DefaultJitter),
            body.KillDate,
            mode,
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
    // endpoint when the request names one (refusing anything that is not
    // this engagement's own listener), the DNS family's typed dial
    // otherwise -- the fronting seam a listener record cannot yet express.
    // The refusal is returned as a string; the value is null only when the
    // error is set. Transport reports the named listener's transport (null
    // for a typed dial) -- the fact the beacon resolution below needs, so
    // a derived contact matches the front it rides.
    private static async Task<(string? Value, string? Transport, string? Trust, string? Error)> ResolveEndpointAsync(
        Endpoints.PayloadEndpoints.BuildPayloadRequest body,
        EngagementId engagementId,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        if (body.ListenerId is not { } listenerIdText)
        {
            var typed = body.Endpoint?.Trim();
            // The typed endpoint is the DNS family's fronting seam alone: a
            // DNS listener's baked dial names its own bind as the resolver,
            // so a fronted resolver (a DoH redirector, a public address over
            // a private bind) is expressible only by typing the dial. Every
            // web or socket front is a listener record -- the public
            // endpoint is free-form there and repoint rotates it -- and a
            // typed address this teamserver cannot verify only bakes an
            // artifact that can never enroll.
            if (string.IsNullOrEmpty(typed))
                return (null, null, null, "Name a listener for the front the implant dials.");
            if (!typed.StartsWith("dns://", StringComparison.OrdinalIgnoreCase)
                && !typed.StartsWith("doh://", StringComparison.OrdinalIgnoreCase))
                return (null, null, null,
                    "A typed endpoint is the DNS family's dial alone (dns://resolver/zone, doh://resolver/zone) "
                    + "-- name a listener for a web or socket front.");
            return (typed, null, null, null);
        }

        if (body.Endpoint is not null)
            return (null, null, null, "Name either listenerId or endpoint, not both.");
        if (!Guid.TryParse(listenerIdText, out var listenerValue))
            return (null, null, null, "ListenerId is not a valid identifier.");

        var listener = await listeners.FindAsync(new ListenerId(listenerValue), cancellationToken);
        if (listener is null)
            return (null, null, null, "ListenerId does not name a listener.");
        if (listener.EngagementId is null)
            return (null, null, null,
                "ListenerId names a shared-tier listener; an implant dials its own engagement's listener.");
        if (listener.EngagementId != engagementId)
            return (null, null, null, "ListenerId names another engagement's listener.");

        // The socket family's enroll arm (Sec 8, enrollment over the stream
        // contact): a tcp listener is enroll-nameable -- the opening exchange
        // on the socket carries the EnrollRequest frames -- and the baked
        // endpoint is the transport's own dial: the host:port under tcp://.
        if (listener.Transport == "tcp")
            return WithTrust(SocketDial(listener.PublicEndpoint), listener.TrustPosture);

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
                return (null, null, null, dnsError);
            return (dial.Dial, listener.Transport, listener.TrustPosture, null);
        }

        if (TransportProviders.Find(listener.Transport) is not KestrelEndpointProvider)
            return (null, null, null,
                $"The {listener.Transport} transport does not serve enrollment; build against an HTTP-shaped listener.");

        // The public endpoint may be the bare host:port redirector shape; the
        // listener's transport names the scheme the implant dials.
        var publicEndpoint = listener.PublicEndpoint.Trim();
        if (Uri.TryCreate(publicEndpoint, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return (publicEndpoint, listener.Transport, listener.TrustPosture, null);
        var scheme = TransportProviders.Find(listener.Transport)?.PublicEndpointScheme ?? "https";
        return ($"{scheme}://{publicEndpoint}", listener.Transport, listener.TrustPosture, null);
    }

    // Threads the socket family's dial through the four-part resolution (a
    // raw-TCP front carries no TLS posture -- its dial is cleartext -- but
    // the listener's record still names one, and the build reads it).
    private static (string? Value, string? Transport, string? Trust, string? Error) WithTrust(
        (string? Value, string? Transport, string? Error) socket, string? trust)
        => (socket.Value, socket.Transport, trust, socket.Error);

    // Resolves the contact the baked artifact runs. A named beacon listener
    // or a typed beacon endpoint names the web front the WebSocket beacon
    // dials, hanging off the schemed front itself. With neither named the
    // contact derives from the enroll front: every web front (http, https,
    // or a typed http(s) URL) carries the envelope POST cycle on its own
    // port -- the mainstream single-port shape, no split required, and the
    // baked mode picks the client.
    private static async Task<(string? Value, string? Error)> ResolveBeaconAsync(
        Endpoints.PayloadEndpoints.BuildPayloadRequest body,
        ImplantClass @class,
        string mode,
        string trust,
        string? enrollTransport,
        string? enrollEndpoint,
        EngagementId engagementId,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        if (body.BeaconListenerId is not null && body.BeaconEndpoint is not null)
            return (null, "Name either beaconListenerId or beaconEndpoint, not both.");
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
            // The carrier shares the front's certificate posture whenever its
            // dial is TLS: the artifact bakes one root set, so a carrier
            // presenting the other posture is a beacon the artifact cannot
            // handshake. A cleartext carrier needs no roots and rides either
            // posture.
            if (DialIsHttps(listener)
                && !string.Equals(listener.TrustPosture, trust, StringComparison.OrdinalIgnoreCase))
                return (null,
                    $"The carrier {listener.Name} presents a '{listener.TrustPosture}' certificate while this build rides '{trust}' roots; name a carrier that shares the front's posture.");
            // The baked beacon URL's shape is the client the artifact dials:
            // the web family's WebSocket beacon hangs off the schemed front
            // itself.
            return (listener.PublicEndpoint, null);
        }

        if (body.BeaconEndpoint is { } beaconEndpoint)
        {
            var trimmed = beaconEndpoint.Trim();
            // The DNS family's manual dials: a resolver and a zone
            // (dns://resolver[:port]/zone, doh://resolver[:port]/zone) --
            // the shape the implant's DNS client parses (the authority is
            // the resolver it queries; there is no system-resolver form).
            // Anything else is the web family's https.
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
                        $"A dns/doh beacon endpoint names a resolver and a zone (dns://resolver:53/zone, doh://resolver:443/zone), got '{beaconEndpoint}'.");
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
        string endpoint,
        string? beaconEndpoint,
        string caPem,
        TlsTrust tlsTrust)
    {
        var profile = new TransportProfile(
            endpoint,
            body.UriPath ?? "/beacon")
        {
            // The split-socket shape: enroll dials one host, the beacon
            // stream another. Null keeps the derived single-front bake.
            BeaconEndpoint = beaconEndpoint,
            // The pinned teamserver CA rides every build.
            CaPem = caPem,
            // Which roots the TLS dials trust: the CA above alone, or the
            // public set for a real-domain front.
            TlsTrust = tlsTrust,
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
            @class = ImplantClass.Implant;
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

    // The egress-walk families (architecture.md Sec 8): the web pair, the
    // DNS pair, and the raw socket. The artifact's contact carriage is fixed
    // by the front's own shape, so a fallback outside the front's family
    // backs the enroll walk alone -- every contact cycle steps over it --
    // and the build refuses the mix instead of baking a dead entry.
    private static string? SchemeFamily(string? endpoint)
    {
        var trimmed = endpoint?.Trim();
        if (trimmed is null || trimmed.Length == 0)
            return null;
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "web";
        if (trimmed.StartsWith("dns://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("doh://", StringComparison.OrdinalIgnoreCase))
            return "dns";
        if (trimmed.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            return "tcp";
        return null;
    }

    // Whether a web-family dial names this listener: verbatim, or completed
    // under the transport's scheme -- the two shapes the picker bakes.
    private static bool NamesFront(string fallback, Rod.Transport.Listeners.Listener listener)
    {
        var trimmed = fallback.Trim();
        if (string.Equals(trimmed, listener.PublicEndpoint.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;
        var scheme = listener.Transport is "https" or "mtls" ? "https" : "http";
        return string.Equals(trimmed, $"{scheme}://{listener.PublicEndpoint.Trim()}", StringComparison.OrdinalIgnoreCase);
    }

    // Whether the listener's dial is TLS: an absolute https public endpoint,
    // or a bare one completed under an https-family transport.
    private static bool DialIsHttps(Rod.Transport.Listeners.Listener listener)
    {
        var pub = listener.PublicEndpoint.Trim();
        if (Uri.TryCreate(pub, UriKind.Absolute, out var abs)
            && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            return abs.Scheme == Uri.UriSchemeHttps;
        return listener.Transport is "https" or "mtls";
    }

    // The shapes one family's fallbacks dial, for a refusal that teaches.
    private static string FamilyShapes(string family) => family switch
    {
        "web" => "http(s)://",
        "dns" => "dns:// or doh://",
        "tcp" => "tcp://",
        _ => "the front's scheme",
    };

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
