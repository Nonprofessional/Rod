using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rod.CoreState.Engagements;

namespace Rod.CoreState.Pki;

/// <summary>
/// Self-signed development <see cref="IImplantCertificateAuthority"/> for dev
/// runs and tests. Generates a throwaway CA root once at construction; the
/// root is the tasking signer and the issuer of the listeners' TLS server
/// leaves. Not for production: the CA key lives in process memory and is
/// non-rotatable. Real deployments substitute an externally provisioned,
/// per-engagement CA behind the same port.
/// </summary>
public sealed class DevCertificateAuthority : IImplantCertificateAuthority
{
    // The CA root and the listener server leaf stay RSA (signing and serving
    // keys, an ops concern).
    private const int RsaKeySize = 2048;
    private static readonly TimeSpan CaLifetime = TimeSpan.FromDays(365);

    // Pinned empirically, not a free knob: a 365-day server leaf here made
    // the DoH e2e legs drop TLS handshakes intermittently (isolated by
    // file-by-file bisection; 30 days ran 24 consecutive passes, and no
    // mechanism was established). Lengthen only with those legs re-proven.
    private static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(30);

    private readonly X509Certificate2 _caCertificate;
    private readonly RSA _caKey;
    private readonly object _serverCertificateLock = new();
    private X509Certificate2? _serverCertificate;

    public DevCertificateAuthority()
    {
        _caKey = RSA.Create(RsaKeySize);
        _caCertificate = BuildCaCertificate(_caKey);
    }

    /// <summary>
    /// The CA root, for the transport layer to trust when terminating mTLS
    /// (architecture.md Sec 9). The caller must not dispose the returned copy
    /// independently of this authority's lifetime.
    /// </summary>
    public X509Certificate2 GetCaCertificate() => _caCertificate;

    /// <summary>
    /// The listener server leaf, minted on first use and then reused for every
    /// connection -- see the interface contract for why the CA's own root cannot
    /// ride this position on Windows.
    /// </summary>
    public X509Certificate2 GetServerCertificate()
    {
        lock (_serverCertificateLock)
        {
            _serverCertificate ??= BuildServerCertificate();
            return _serverCertificate;
        }
    }

    public byte[] SignTasking(string implantId, string taskId, string verb, string arguments, ulong? nonce = null)
        => _caKey.SignData(
            TaskingCanonical.Bytes(implantId, taskId, verb, arguments, nonce),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

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

    // Builds and signs the TLS server leaf the listeners present. It is the
    // mirror of an implant leaf on the server side: end-entity,
    // digitalSignature/keyEncipherment, server-auth EKU -- the usage set
    // SChannel demands before it will shake hands with us.
    private X509Certificate2 BuildServerCertificate()
    {
        using var key = RSA.Create(RsaKeySize);
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

        var notBefore = DateTimeOffset.UtcNow;
        var leaf = request.Create(_caCertificate, notBefore, notBefore + LeafLifetime, Guid.NewGuid().ToByteArray());

        // The listener signs handshakes with this key, and SChannel needs it in
        // a presentable (persisted) shape -- see SChannelCertificate.
        return SChannelCertificate.WithUsableKey(leaf, key);
    }
}
