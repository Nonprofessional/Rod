using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Rod.CoreState.Pki;

/// <summary>
/// The custom X.509 extension that carries an implant's
/// <c>engagement_id</c> alongside the certificate subject's
/// <c>CN=implant_id</c> -- together binding <c>(implant_id, engagement_id)</c>
/// as required by architecture.md Sec 9. The leaf's common name carries the
/// implant id; this extension carries the engagement id so the binding is
/// structured and tamper-evident (covered by the certificate signature) rather
/// than a free-form name string.
///
/// The extension value is the engagement id as UTF-8 bytes. This is an internal
/// Rod convention shared by the dev CA, the test harness, and () the mTLS
/// identity check; the value is integrity-protected by the certificate
/// signature regardless of its inner encoding.
/// </summary>
public static class RodImplantEngagementExtension
{
    /// <summary>
    /// Private OID identifying the Rod engagement-id binding extension. Stable
    /// for the life of the certificate contract.
    /// </summary>
    public const string Oid = "1.3.6.1.4.1.65535.1.1";

    private static readonly Oid s_oid = new(Oid, "Rod Engagement Binding");

    /// <summary>
    /// Builds the <see cref="X509Extension"/> carrying <paramref name="engagementId"/>.
    /// </summary>
    public static X509Extension Build(string engagementId)
        => new(s_oid, Encoding.UTF8.GetBytes(engagementId), critical: false);

    /// <summary>
    /// Reads the engagement id from the extension, if present on
    /// <paramref name="certificate"/>. Returns false when the extension is absent
    /// or malformed.
    /// </summary>
    public static bool TryRead(X509Certificate2 certificate, out string engagementId)
    {
        engagementId = string.Empty;
        foreach (var ext in certificate.Extensions)
        {
            if (ext.Oid?.Value != Oid)
                continue;

            try
            {
                engagementId = Encoding.UTF8.GetString(ext.RawData);
                return engagementId.Length > 0;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        return false;
    }
}
