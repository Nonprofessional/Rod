using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rod.CoreState.Engagements;

namespace Rod.CoreState.Pki;

/// <summary>
/// Self-signed development <see cref="IImplantCertificateAuthority"/> for dev
/// runs and tests. Generates a throwaway CA root once at construction and signs
/// each implant leaf with it, binding <c>(implant_id, engagement_id)</c>: the
/// leaf subject's common name is the implant id, and a Rod custom extension
/// carries the engagement id. Not for production: the CA key lives in process
/// memory and is non-rotatable. Real deployments substitute an externally
/// provisioned, per-engagement CA behind the same port.
/// </summary>
public sealed class DevCertificateAuthority : IImplantCertificateAuthority
{
    // RSA key sizes; production values are an ops concern.
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
        // The leaf key is the implant's own; it is not retained server-side after
        // the certificate is returned (the implant owns its private key).
        using var leafKey = RSA.Create(LeafKeySize);
        return Task.FromResult(IssueLeaf(subject, leafKey));
    }

    public Task<IssuedCertificate> IssueWithKeyAsync(
        ImplantCertificateSubject subject,
        RSA leafPrivateKey,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IssueLeaf(subject, leafPrivateKey));

    public Task<IssuedCertificate> IssueWithPublicKeyAsync(
        ImplantCertificateSubject subject,
        RSA leafPublicKey,
        CancellationToken cancellationToken = default)
    {
        // Re-import only the public parameters so the signing path can never see,
        // or accidentally retain, the caller's private key. The CA key signs the
        // leaf; the leaf's public key is the implant's, bound to its engagement
        // (architecture.md Sec 9). CertificateRequest accepts a public-only RSA.
        var publicParams = leafPublicKey.ExportParameters(includePrivateParameters: false);
        using var publicKeyOnly = RSA.Create();
        publicKeyOnly.ImportParameters(publicParams);
        return Task.FromResult(IssueLeaf(subject, publicKeyOnly));
    }

    /// <summary>
    /// The CA root, for the transport layer to trust when terminating mTLS
    /// (architecture.md Sec 9). The caller must not dispose the returned copy
    /// independently of this authority's lifetime.
    /// </summary>
    public X509Certificate2 GetCaCertificate() => _caCertificate;

    public byte[] SignTasking(string implantId, string taskId, string verb, string arguments, ulong? nonce = null)
        => _caKey.SignData(
            TaskingCanonical.Bytes(implantId, taskId, verb, arguments, nonce),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

    // Builds and signs an implant leaf over the supplied key material, binding
    // (implant_id, engagement_id). The CA key signs; the leaf's public key is
    // whatever the supplied RSA carries -- a full key pair (IssueAsync/
    // IssueWithKeyAsync) or a public-only RSA (IssueWithPublicKeyAsync, the wire
    // enroll path). CertificateRequest needs only the public half to populate the
    // leaf; the private half never has to be present here.
    private IssuedCertificate IssueLeaf(ImplantCertificateSubject subject, RSA leafKey)
    {
        var implantId = subject.ImplantId.ToString();
        var engagementId = subject.EngagementId.ToString();

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

        return new IssuedCertificate(
            leaf.Export(X509ContentType.Cert),
            new[] { _caCertificate.Export(X509ContentType.Cert) });
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
