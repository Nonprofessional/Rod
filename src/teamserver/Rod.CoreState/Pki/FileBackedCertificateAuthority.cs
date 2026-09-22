using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rod.CoreState.Engagements;

namespace Rod.CoreState.Pki;

/// <summary>
/// The production engagement CA: an externally provisioned certificate and
/// RSA private key loaded from PEM files on disk (architecture.md Sec 9),
/// behind the same ports the dev authority serves -- the tasking signer,
/// the https fronts' server-leaf issuer, and the enrollment chain's root.
/// The CA is provisioned out-of-band by the operator's PKI; this authority
/// consumes it, it never generates the CA itself.
/// </summary>
/// <remarks>
/// The loaded CA certificate and key are held for the singleton lifetime.
/// Construction is eager and validates the inputs, so a missing, unreadable, or
/// mismatched CA fails the host at startup rather than at the first enrollment.
/// </remarks>
public sealed class FileBackedCertificateAuthority : IImplantCertificateAuthority
{
    private readonly X509Certificate2 _caCertificate;

    // Pinned empirically, not a free knob: a 365-day server leaf made the
    // DoH e2e legs drop TLS handshakes intermittently (isolated by
    // file-by-file bisection; 30 days ran 24 consecutive passes, and no
    // mechanism was established). Lengthen only with those legs re-proven.
    private static readonly TimeSpan ServerLeafLifetime = TimeSpan.FromDays(30);
    private readonly object _serverLeafLock = new();
    private readonly Dictionary<string, X509Certificate2> _serverLeaves = new();

    /// <param name="options">
    /// The on-disk CA material. Both paths are required; the passphrase is
    /// optional and only consulted for an encrypted private key.
    /// </param>
    public FileBackedCertificateAuthority(FileBackedCertificateAuthorityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.CaCertificatePath))
            throw new ArgumentException("A CA certificate path is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.CaPrivateKeyPath))
            throw new ArgumentException("A CA private key path is required.", nameof(options));

        // Load the CA certificate (PEM, cert-only). CreateFromPem parses the
        // CERTIFICATE block and ignores anything else; the key is loaded
        // separately below so an encrypted key can be decrypted with the
        // passphrase. (CreateFromPemFile would look for the key in the cert file
        // when its key argument is omitted, so it does not suit a cert-only file.)
        using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(options.CaCertificatePath));
        using var certPublic = certificate.GetRSAPublicKey()
            ?? throw new InvalidOperationException(
                "The configured CA certificate does not carry an RSA public key; " +
                "RSA is the only supported CA key type.");

        // Load the CA private key (PEM, PKCS#1 or PKCS#8). ImportFromPem and
        // ImportFromEncryptedPem are the shapes an externally provisioned CA key
        // is normally distributed in.
        using var key = RSA.Create();
        var keyPem = File.ReadAllText(options.CaPrivateKeyPath);
        if (!string.IsNullOrEmpty(options.CaPrivateKeyPassphrase))
            key.ImportFromEncryptedPem(keyPem, options.CaPrivateKeyPassphrase);
        else
            key.ImportFromPem(keyPem);

        // Fail fast when the key does not belong to the certificate. Signing with
        // a mismatched key would produce leaves that cannot chain to this CA, so
        // surface the misconfiguration at startup, not at the first enrollment.
        if (!PublicKeysMatch(certPublic, key))
            throw new InvalidOperationException(
                "The configured CA private key does not match the CA certificate.");

        // Attach the key to the certificate so CertificateRequest.Create can sign
        // with it (the issuer certificate supplies the signing key, the same way
        // the dev CA's CreateSelfSigned root carries its own key). The result is
        // an independent copy; the originals above dispose with the ctor scope.
        _caCertificate = certificate.CopyWithPrivateKey(key);
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
    /// Windows. An operator who wants a provisioned server identity instead
    /// fronts the listener with their own certificate at the transport seam;
    /// this is the self-sufficient default.
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
                leaf = ServerLeaf.Build(_caCertificate, host, ServerLeafLifetime);
                _serverLeaves[host] = leaf;
            }

            return leaf;
        }
    }

    public byte[] SignTasking(string implantId, string taskId, string verb, string arguments, ulong? nonce = null)
    {
        // The ctor attached the loaded CA private key to the retained copy, so
        // the same key that issues the listeners' server leaves signs
        // dispatched tasking (architecture.md Sec 9).
        using var key = _caCertificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("The CA certificate does not carry an RSA private key.");
        return key.SignData(
            TaskingCanonical.Bytes(implantId, taskId, verb, arguments, nonce),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    // True when both RSAs present the same public parameters (modulus + exponent).
    // Used to confirm the loaded private key belongs to the loaded certificate.
    private static bool PublicKeysMatch(RSA certificatePublic, RSA privateKey)
    {
        var a = certificatePublic.ExportParameters(includePrivateParameters: false);
        var b = privateKey.ExportParameters(includePrivateParameters: false);
        return SameBytes(a.Modulus, b.Modulus) && SameBytes(a.Exponent, b.Exponent);

        static bool SameBytes(byte[]? x, byte[]? y)
            => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);
    }
}
