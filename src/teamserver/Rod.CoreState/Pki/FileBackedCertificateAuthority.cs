using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rod.CoreState.Engagements;

namespace Rod.CoreState.Pki;

/// <summary>
/// The production <see cref="IImplantCertificateAuthority"/>: signs implant
/// leaves with an externally provisioned CA loaded from PEM files on disk, so
/// enrollment binds <c>(implant_id, engagement_id)</c> to a non-dev CA chain
/// (architecture.md Sec 9). The CA certificate and its RSA private key are
/// provisioned out-of-band by the operator's PKI and supplied via configuration;
/// this authority consumes them, it never generates the CA itself. The RSA CA
/// key signs ECDSA P-256 implant leaves -- the CA's signing algorithm and the
/// leaf's algorithm are independent, the standard cross-algorithm PKI shape.
/// Leaf construction is identical to <see cref="DevCertificateAuthority"/> --
/// only the issuer changes -- so every identity and handshake invariant is
/// preserved.
/// </summary>
/// <remarks>
/// The loaded CA certificate and key are held for the singleton lifetime.
/// Construction is eager and validates the inputs, so a missing, unreadable, or
/// mismatched CA fails the host at startup rather than at the first enrollment.
/// </remarks>
public sealed class FileBackedCertificateAuthority : IImplantCertificateAuthority
{
    private static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(30);
    private static readonly ECCurve LeafCurve = ECCurve.NamedCurves.nistP256;

    private readonly X509Certificate2 _caCertificate;
    private readonly object _serverCertificateLock = new();
    private X509Certificate2? _serverCertificate;

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

    public Task<IssuedCertificate> IssueAsync(
        ImplantCertificateSubject subject,
        CancellationToken cancellationToken = default)
    {
        // The leaf key is the implant's own; it is not retained server-side after
        // the certificate is returned (the implant owns its private key).
        using var leafKey = ECDsa.Create(LeafCurve);
        return Task.FromResult(IssueLeaf(subject, leafKey));
    }

    public Task<IssuedCertificate> IssueWithKeyAsync(
        ImplantCertificateSubject subject,
        ECDsa leafPrivateKey,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IssueLeaf(subject, leafPrivateKey));

    public Task<IssuedCertificate> IssueWithPublicKeyAsync(
        ImplantCertificateSubject subject,
        ECDsa leafPublicKey,
        CancellationToken cancellationToken = default)
    {
        // Re-import only the public parameters so the signing path can never see,
        // or accidentally retain, the caller's private key. CertificateRequest
        // accepts a public-only ECDsa; the CA key signs, the leaf's public key is
        // the implant's, bound to its engagement (architecture.md Sec 9).
        var publicParams = leafPublicKey.ExportParameters(includePrivateParameters: false);
        using var publicKeyOnly = ECDsa.Create();
        publicKeyOnly.ImportParameters(publicParams);
        return Task.FromResult(IssueLeaf(subject, publicKeyOnly));
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
    /// ride this position on Windows. An operator who wants a provisioned server
    /// identity instead fronts the listener with their own certificate at the
    /// transport seam; this is the self-sufficient default.
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
    {
        // The ctor attached the loaded CA private key to the retained copy, so
        // the same key that signs implant leaves signs dispatched tasking
        // (architecture.md Sec 9).
        using var key = _caCertificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("The CA certificate does not carry an RSA private key.");
        return key.SignData(
            TaskingCanonical.Bytes(implantId, taskId, verb, arguments, nonce),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    // Builds and signs an implant leaf over the supplied key material, binding
    // (implant_id, engagement_id). The CA key signs; the leaf's public key is
    // whatever the supplied ECDsa carries. Identical to the dev authority's leaf
    // construction -- only the issuer differs -- so the subject DN, the SAN
    // identity entries, the client-auth EKU, and the end-entity basic
    // constraints all match what the handshake and identity checks expect.
    private IssuedCertificate IssueLeaf(ImplantCertificateSubject subject, ECDsa leafKey)
    {
        var implantId = subject.ImplantId.ToString();
        var engagementId = subject.EngagementId.ToString();

        var notBefore = DateTimeOffset.UtcNow;
        var notAfter = notBefore + LeafLifetime;

        // Every implant leaf shares this fixed conventional subject; the ids
        // ride the SAN entries below. A GUID common name is itself a toolchain
        // fingerprint, on the wire and in host forensics.
        var subjectDn = "CN=rod-implant,O=Rod,C=ZZ";
        var request = new CertificateRequest(subjectDn, leafKey, HashAlgorithmName.SHA256);

        // An implant leaf is an end-entity certificate: not a CA, may not sign
        // others. DigitalSignature alone -- keyEncipherment is the RSA
        // key-transport bit, which no conventional EC service certificate
        // carries.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.2", "Client Authentication") }, // TLS client auth.
                critical: true));
        // The (implant_id, engagement_id) binding -- the half of the leaf an
        // ordinary service certificate would carry in SANs, not in the DN.
        request.CertificateExtensions.Add(ImplantSubjectAlternativeNames.Build(implantId, engagementId));

        // A random serial, the conventional shape -- not the implant id's bytes,
        // which would republish the identity in one more field.
        var serial = Guid.NewGuid().ToByteArray();
        // The generator overload signs cross-algorithm: the loaded CA key is
        // RSA while the leaf key is ECDSA, a pair the issuerCertificate
        // overload refuses. X.509 itself carries no such restriction -- any CA
        // algorithm signing any leaf algorithm is the standard PKI shape.
        using var caKey = _caCertificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("The CA certificate does not carry an RSA private key.");
        var leaf = request.Create(
            _caCertificate.SubjectName,
            X509SignatureGenerator.CreateForRSA(caKey, RSASignaturePadding.Pkcs1),
            notBefore, notAfter, serial);

        return new IssuedCertificate(
            leaf.Export(X509ContentType.Cert),
            new[] { _caCertificate.Export(X509ContentType.Cert) });
    }

    // Builds and signs the TLS server leaf the listeners present: end-entity,
    // digitalSignature/keyEncipherment, server-auth EKU -- the usage set
    // SChannel demands before it will shake hands with us. Mirrors the dev
    // authority's server leaf so both issuers present the same shape.
    private X509Certificate2 BuildServerCertificate()
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

        var notBefore = DateTimeOffset.UtcNow;
        var leaf = request.Create(_caCertificate, notBefore, notBefore + LeafLifetime, Guid.NewGuid().ToByteArray());

        // The listener signs handshakes with this key, and SChannel needs it in
        // a presentable (persisted) shape -- see SChannelCertificate.
        return SChannelCertificate.WithUsableKey(leaf, key);
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
