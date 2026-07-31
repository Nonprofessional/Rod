using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rod.CoreState.Engagements;

namespace Rod.CoreState.Pki;

/// <summary>
/// Self-signed development <see cref="IImplantCertificateAuthority"/> for the
/// walking skeleton. Generates a throwaway CA root once at construction and signs
/// each implant leaf with it, binding <c>(implant_id, engagement_id)</c>: the
/// leaf subject's common name is the implant id, and a Rod custom extension
/// carries the engagement id. Not for production: the CA key lives in process
/// memory and is non-rotatable. Real deployments substitute an externally
/// provisioned, per-engagement CA behind the same port.
/// </summary>
public sealed class DevCertificateAuthority : IImplantCertificateAuthority
{
    // RSA key sizes for the skeleton; production values are an ops concern.
    private const int CaKeySize = 2048;
    private const int LeafKeySize = 2048;
    private static readonly TimeSpan CaLifetime = TimeSpan.FromDays(365);
    private static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(30);

    private readonly X509Certificate2 _caCertificate;
    private readonly RSA _caKey;

    public DevCertificateAuthority()
    {
        _caKey = RSA.Create(CaKeySize);
        _caCertificate = BuildCaCertificate(_caKey);
    }

    public Task<IssuedCertificate> IssueAsync(
        ImplantCertificateSubject subject,
        CancellationToken cancellationToken = default)
    {
        var implantId = subject.ImplantId.ToString();
        var engagementId = subject.EngagementId.ToString();

        // The leaf key is the implant's own; it is not retained server-side after
        // the certificate is returned (the implant owns its private key).
        using var leafKey = RSA.Create(LeafKeySize);
        var notBefore = DateTimeOffset.UtcNow;
        var notAfter = notBefore + LeafLifetime;

        var subjectDn = $"CN={implantId}";
        var request = new CertificateRequest(subjectDn, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // An implant leaf is an end-entity certificate: not a CA, may not sign others.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.2", "Client Authentication") }, // TLS client auth.
                critical: true));
        // The engagement binding -- the half of (implant_id, engagement_id) that
        // does not fit in the subject DN.
        request.CertificateExtensions.Add(RodImplantEngagementExtension.Build(engagementId));

        var serial = subject.ImplantId.Value.ToByteArray();
        var leaf = request.Create(_caCertificate, notBefore, notAfter, serial);

        return Task.FromResult<IssuedCertificate>(new IssuedCertificate(
            leaf.Export(X509ContentType.Cert),
            new[] { _caCertificate.Export(X509ContentType.Cert) }));
    }

    // Builds a self-signed CA root: CA:TRUE, key-cert-sign, self-issued.
    private static X509Certificate2 BuildCaCertificate(RSA caKey)
    {
        var notBefore = DateTimeOffset.UtcNow;
        var notAfter = notBefore + CaLifetime;

        var subjectDn = "CN=Rod Dev CA,O=Rod,C=ZZ";
        var request = new CertificateRequest(subjectDn, caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                critical: true));

        // CreateSelfSigned produces a self-issued root: subject == issuer.
        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
