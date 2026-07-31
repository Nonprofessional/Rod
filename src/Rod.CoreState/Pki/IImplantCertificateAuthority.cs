using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rod.CoreState.Pki;

/// <summary>
/// Issues the client certificate that binds an implant to its engagement
/// (architecture.md Sec 9 -- mTLS; an implant certificate binds
/// <c>(implant_id, engagement_id)</c>). The walking skeleton ships a self-signed
/// dev CA; production rotates to an externally provisioned engagement CA without
/// changing this contract.
/// </summary>
public interface IImplantCertificateAuthority
{
    /// <summary>
    /// Issues a leaf certificate for the given implant, bound to its engagement,
    /// and returns it with the CA chain the implant needs to present/verify.
    /// Certificates are DER-encoded. The CA generates the leaf's key pair and
    /// discards the private key after signing -- use this when the caller does
    /// not need to act as the certificate's subject (e.g. the enroll response).
    /// </summary>
    Task<IssuedCertificate> IssueAsync(
        ImplantCertificateSubject subject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a leaf certificate over a caller-supplied key pair, so the caller
    /// keeps the private key and can present the certificate in an mTLS handshake
    /// (architecture.md Sec 9). Same engagement binding as
    /// <see cref="IssueAsync(ImplantCertificateSubject, CancellationToken)"/>; the
    /// returned leaf is DER-encoded and the caller owns its private key.
    /// </summary>
    Task<IssuedCertificate> IssueWithKeyAsync(
        ImplantCertificateSubject subject,
        RSA leafPrivateKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The CA root certificate, DER-encoded. Held out so the transport layer can
    /// trust it when terminating mTLS (architecture.md Sec 9): a presenting
    /// client certificate is accepted only when it chains to this root.
    /// </summary>
    X509Certificate2 GetCaCertificate();
}

/// <summary>The identity to bind into an issued implant certificate.</summary>
public sealed record ImplantCertificateSubject(ImplantId ImplantId, EngagementId EngagementId);

/// <summary>
/// A leaf implant certificate and the CA chain (root first) needed to validate
/// it. All entries are DER-encoded.
/// </summary>
public sealed record IssuedCertificate(byte[] Leaf, IReadOnlyList<byte[]> CaChain);
