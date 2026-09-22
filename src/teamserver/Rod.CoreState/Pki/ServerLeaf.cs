using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rod.CoreState.Pki;

/// <summary>
/// The TLS server leaf both authorities issue: end-entity,
/// digitalSignature/keyEncipherment, server-auth EKU -- the usage set
/// SChannel demands before it will shake hands -- and a SAN naming the host
/// clients dial, because the reference implant's rustls client runs full
/// webpki validation against the CA it pinned, server-name matching
/// included: a nameless leaf strands every https front. The CN is a fixed
/// label; the SAN is the machine-read identity.
/// </summary>
public static class ServerLeaf
{
    /// <summary>
    /// Builds and signs one leaf. The issuer certificate must carry its
    /// signing key (the shape both authorities retain).
    /// </summary>
    internal static X509Certificate2 Build(X509Certificate2 issuer, string host, TimeSpan lifetime)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=rod-listener,O=Rod,C=ZZ", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1", "Server Authentication") }, // TLS server auth.
                critical: true));

        // An IP literal dials as an IP SAN, a hostname as a DNS SAN; an
        // empty host mints the nameless shape for clients that do no name
        // matching.
        var trimmed = host.Trim();
        if (trimmed.Length > 0)
        {
            var names = new SubjectAlternativeNameBuilder();
            if (IPAddress.TryParse(trimmed, out var address))
                names.AddIpAddress(address);
            else
                names.AddDnsName(trimmed);
            request.CertificateExtensions.Add(names.Build());
        }

        var notBefore = DateTimeOffset.UtcNow;
        var leaf = request.Create(issuer, notBefore, notBefore + lifetime, Guid.NewGuid().ToByteArray());

        // The listener signs handshakes with this key, and SChannel needs it in
        // a presentable (persisted) shape -- see SChannelCertificate.
        return SChannelCertificate.WithUsableKey(leaf, key);
    }

    /// <summary>
    /// The host clients dial a front on: the public endpoint's host, parsed
    /// from a schemed URL or a bare host(:port). Text that parses no further
    /// is returned verbatim -- the SAN is then best effort rather than a
    /// failed bind.
    /// </summary>
    public static string HostOf(string publicEndpoint)
    {
        var text = publicEndpoint.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Uri.TryCreate("https://" + text, UriKind.Absolute, out uri);
        }

        var host = uri?.Host;
        return string.IsNullOrEmpty(host) ? text : host;
    }
}
