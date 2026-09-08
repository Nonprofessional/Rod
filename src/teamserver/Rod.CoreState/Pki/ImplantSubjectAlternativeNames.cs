using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

namespace Rod.CoreState.Pki;

/// <summary>
/// Builds and reads the URI subject-alternative-name entries that bind an
/// implant's <c>(implant_id, engagement_id)</c> into its leaf certificate
/// (architecture.md Sec 9). Workload identity in URI SAN entries is the shape
/// legitimate service certificates use -- SPIFFE-style <c>spiffe://</c> URIs
/// with a labeled path segment per id -- whereas a GUID common name or a
/// custom-OID extension is itself a toolchain fingerprint, on the wire and in
/// host forensics. The binding is tamper-evident the same way the retired
/// custom extension was: the entries sit inside the certificate the CA
/// signed.
/// </summary>
public static class ImplantSubjectAlternativeNames
{
    private const string Scheme = "spiffe";
    private const string TrustDomain = "rod";
    private const string ImplantLabel = "implant";
    private const string EngagementLabel = "engagement";

    // GeneralName choice [6], the URI entry of a subject alternative name.
    private static readonly Asn1Tag UriTag = new(TagClass.ContextSpecific, 6);

    /// <summary>
    /// The SAN extension carrying the ids as two labeled URI entries:
    /// <c>spiffe://rod/implant/&lt;implant_id&gt;</c> and
    /// <c>spiffe://rod/engagement/&lt;engagement_id&gt;</c>. Non-critical: the
    /// leaf keeps a non-empty conventional subject, so RFC 5280 does not
    /// require a critical SAN.
    /// </summary>
    public static X509Extension Build(string implantId, string engagementId)
    {
        var builder = new SubjectAlternativeNameBuilder();
        builder.AddUri(new Uri($"{Scheme}://{TrustDomain}/{ImplantLabel}/{implantId}"));
        builder.AddUri(new Uri($"{Scheme}://{TrustDomain}/{EngagementLabel}/{engagementId}"));
        return builder.Build(critical: false);
    }

    /// <summary>
    /// Reads the implant and engagement ids back out of
    /// <paramref name="certificate"/>'s URI SAN entries, matching entries by
    /// label so their order never matters. Returns false when either labeled
    /// entry is absent or the SAN cannot be read.
    /// </summary>
    public static bool TryRead(X509Certificate2 certificate, out string implantId, out string engagementId)
    {
        implantId = string.Empty;
        engagementId = string.Empty;

        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null)
            return false;

        // The in-box SAN type enumerates only DNS names and IP addresses, so
        // walk the extension's DER directly: each GeneralName choice [6] is a
        // URI, and every other entry is skipped.
        string? readImplant = null;
        string? readEngagement = null;
        var outer = new AsnReader(san.RawData, AsnEncodingRules.DER);
        var sequence = outer.ReadSequence();
        while (sequence.HasData)
        {
            if (sequence.PeekTag() != UriTag)
            {
                sequence.ReadEncodedValue();
                continue;
            }

            var uri = sequence.ReadCharacterString(UniversalTagNumber.IA5String, UriTag);
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                || !parsed.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Path shape: /<label>/<id>.
            var parts = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                continue;
            if (parts[0] == ImplantLabel)
                readImplant = parts[1];
            else if (parts[0] == EngagementLabel)
                readEngagement = parts[1];
        }

        if (readImplant is null || readEngagement is null)
            return false;

        implantId = readImplant;
        engagementId = readEngagement;
        return true;
    }
}
