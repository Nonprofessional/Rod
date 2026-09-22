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
    private readonly object _serverLeafLock = new();
    private readonly Dictionary<string, X509Certificate2> _serverLeaves = new();

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
    /// The listener server leaf for the named host, minted on first use,
    /// reused for every connection to that host, and re-minted when it nears
    /// expiry (<see cref="ServerLeafRotation"/>) -- see the interface
    /// contract for why the CA's own root cannot ride this position on
    /// Windows.
    /// </summary>
    public X509Certificate2 GetServerCertificate(string host)
    {
        lock (_serverLeafLock)
        {
            // Kestrel's ServerCertificateSelector asks per handshake, so the
            // re-mint reaches every new connection. The replaced leaf is
            // dropped, not disposed: a handshake that already selected it may
            // still be reading it. The cache is keyed by host and bounded by
            // the front count -- one entry per distinct dialed name.
            if (!_serverLeaves.TryGetValue(host, out var leaf)
                || ServerLeafRotation.Due(leaf, DateTimeOffset.UtcNow))
            {
                leaf = ServerLeaf.Build(_caCertificate, host, LeafLifetime);
                _serverLeaves[host] = leaf;
            }

            return leaf;
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
}
