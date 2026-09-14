namespace Rod.Audit;

/// <summary>
/// A built implant payload awaiting retrieval by the operator (architecture.md
/// Sec 6; storage &amp; audit layer). Payloads are engagement-scoped evidence-grade
/// objects like <see cref="Artifact"/>, but they are not attached to a task: a
/// payload is generated before any implant enrolls, so it carries the engagement
/// and the requesting operator's build configuration instead of a task binding.
/// <see cref="Class"/> and <see cref="Language"/> are kept as strings so the
/// audit layer stays free of core-state and build-pipeline types -- the
/// innermost ring crosses the layer boundary with primitives only.
/// </summary>
/// <param name="Target">
/// The build target as <c>os/arch</c> (e.g. <c>linux/amd64</c>), for the
/// operator's library view and filtering. Null on records written before the
/// field existed.
/// </param>
/// <param name="Endpoint">
/// The dial address baked into the artifact (the listener's public endpoint),
/// so the library reads which front a payload phones. Null on old records.
/// </param>
/// <param name="BeaconEndpoint">
/// The host the baked artifact's check-in stream dials, when it differs from
/// <see cref="Endpoint"/> (the split-socket shape: cleartext enroll listener,
/// mTLS beacon listener). Null is the single-front shape -- and every record
/// built before the field existed.
/// </param>
/// <param name="TokenId">
/// The enrollment credential minted for and baked into this artifact: enough
/// to revoke it from the library view, never enough to reuse it. Null when the
/// build was credential-free, or on old records.
/// </param>
/// <param name="EnvelopeKeyId">
/// The id of the per-artifact AES-GCM envelope key, when the artifact's
/// profile carries the AesGcm envelope; the enroll decode resolves the key by
/// it. The pair lives exactly as long as this record: deleting the payload
/// deletes the key, and that artifact's envelopes stop being decodable. Null
/// on every other envelope and on old records.
/// </param>
/// <param name="EnvelopeKey">
/// The teamserver's half of the envelope key pair (the artifact carries the
/// same key baked in). Metadata-sized, so it rides the jsonl line.
/// </param>
/// <param name="Build">
/// The build parameters as they were at bake time -- the beacon profile, the
/// credential's minted budget, and the wire knobs the operator set. The
/// library's detail view reads this so "what did I build" never depends on
/// remembering the form. Null on records written before the snapshot existed;
/// every field inside is nullable so a future knob snapshots without breaking
/// old files.
/// </param>
public sealed record PayloadRecord(
    Guid PayloadId,
    Guid EngagementId,
    string Class,
    string Language,
    string ContentType,
    string Fingerprint,
    byte[] Content,
    long Size,
    DateTimeOffset BuiltAt,
    string? Target = null,
    string? Endpoint = null,
    string? BeaconEndpoint = null,
    Guid? TokenId = null,
    Guid? EnvelopeKeyId = null,
    byte[]? EnvelopeKey = null,
    PayloadBuildProfile? Build = null);

/// <summary>
/// The bake-time snapshot of a payload's build parameters (the values the
/// Build form carried when the artifact was generated). Every field is
/// nullable: the snapshot outlives form redesigns, and an old jsonl line
/// simply reads null for a knob it never recorded. The presence semantics
/// match the build request -- null means "the build's default", never a
/// distinctive value.
/// </summary>
public sealed record PayloadBuildProfile
{
    /// <summary>How the artifact checks in: "stream" (persistent mTLS) or
    /// "poll" (envelope POST cycles).</summary>
    public string? Mode { get; init; }

    /// <summary>
    /// Whether the bake opted into the degraded channel discipline
    /// (architecture.md Sec 10.3): channel verbs claim over the poll
    /// check-ins, at the cycle's latency. Null on records that predate the
    /// flag.
    /// </summary>
    public bool? DegradedChannels { get; init; }

    /// <summary>The check-in interval in seconds.</summary>
    public double? SleepSeconds { get; init; }

    /// <summary>The random slack added to every interval, in seconds.</summary>
    public double? JitterSeconds { get; init; }

    /// <summary>The artifact's expiry fuse.</summary>
    public DateTimeOffset? KillDate { get; init; }

    /// <summary>How many enrolls the baked credential was minted for.</summary>
    public int? TokenMaxUses { get; init; }

    /// <summary>The URI path of the one-time registration POST.</summary>
    public string? EnrollPath { get; init; }

    /// <summary>The User-Agent the implant presents.</summary>
    public string? UserAgent { get; init; }

    /// <summary>The per-request HTTP timeout in seconds.</summary>
    public double? RequestTimeoutSeconds { get; init; }

    /// <summary>The enroll body shape: None, Base64, or AesGcm.</summary>
    public string? Envelope { get; init; }

    /// <summary>Whether check-in bodies seal under the per-artifact key.</summary>
    public bool? CheckInProtection { get; init; }

    /// <summary>The backup dial addresses baked behind the primary, in walk order.</summary>
    public IReadOnlyList<string>? FallbackEndpoints { get; init; }
}
