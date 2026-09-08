namespace Rod.CoreState.Pki;

/// <summary>
/// The on-disk location of the externally provisioned implant CA, bound from the
/// <c>Pki</c> configuration section by the transport composition root. Production
/// substitutes an engagement CA provisioned out-of-band by the operator's PKI for
/// the default self-signed <see cref="DevCertificateAuthority"/>
/// (architecture.md Sec 9); this is the shape that swap reads.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CaCertificatePath"/> is a PEM-encoded CA certificate;
/// <see cref="CaPrivateKeyPath"/> is the matching PEM-encoded RSA private key
/// (PKCS#1 or PKCS#8). <see cref="CaPrivateKeyPassphrase"/> decrypts an encrypted
/// PKCS#8 key when set; leave null for an unencrypted key. Supply it through a
/// secret store or environment variable override in production, never inline in
/// <c>appsettings.json</c>.
/// </para>
/// <para>
/// RSA is the only supported CA key type: it is the CA's signing key, held
/// server-side and never on the wire inside a leaf. The leaves it signs carry
/// the implant's own ECDSA public key (the algorithm-independent PKI shape --
/// an RSA CA signing EC leaves). ECDSA CA keys are a future concern, not a
/// configuration toggle here.
/// </para>
/// </remarks>
public sealed record FileBackedCertificateAuthorityOptions(
    string CaCertificatePath,
    string CaPrivateKeyPath,
    string? CaPrivateKeyPassphrase);
