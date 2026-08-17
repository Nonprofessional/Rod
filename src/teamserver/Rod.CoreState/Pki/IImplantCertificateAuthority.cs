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
    /// Issues a leaf certificate over a caller-supplied <b>public</b> key, so the
    /// caller keeps the matching private key and never transmits it (architecture.md
    /// Sec 9). This is the enrollment path a real implant uses: it generates its own
    /// key pair, sends only the public half with its enroll request, and the CA binds
    /// <c>(implant_id, engagement_id)</c> to a leaf carrying that public key. The CA
    /// signs with its own key; the leaf's public key comes from
    /// <paramref name="leafPublicKey"/>. The returned leaf is DER-encoded; the caller
    /// pairs it with the private key it retained.
    /// </summary>
    /// <remarks>
    /// <paramref name="leafPublicKey"/> carries only public parameters -- the
    /// implementation reads its modulus/exponent and never requires, nor sees, the
    /// private key. Both an implant enrolling over the wire and a test harness
    /// driving enrollment through the same port end here.
    /// </remarks>
    Task<IssuedCertificate> IssueWithPublicKeyAsync(
        ImplantCertificateSubject subject,
        RSA leafPublicKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The CA root certificate, DER-encoded. Held out so the transport layer can
    /// trust it when terminating mTLS (architecture.md Sec 9): a presenting
    /// client certificate is accepted only when it chains to this root.
    /// </summary>
    X509Certificate2 GetCaCertificate();

    /// <summary>
    /// Signs dispatched tasking with the CA's RSA key so an implant acts only
    /// on teamserver-authorized tasks (architecture.md Sec 9 -- command
    /// signing). The signature is RSASSA-PSS over SHA-256 of the canonical
    /// encoding of <c>(implantId, taskId, verb, arguments)</c> (see
    /// <see cref="TaskingCanonical"/>) -- the implant id binds the task to its
    /// intended executor, so a signed frame does not verify on any other
    /// implant. The implant verifies against the CA certificate it already
    /// holds from enrollment or its pinned bundle, so tasking trust rides the
    /// same key as enrollment trust and no new key distribution is needed.
    /// </summary>
    byte[] SignTasking(string implantId, string taskId, string verb, string arguments);
}

/// <summary>The identity to bind into an issued implant certificate.</summary>
public sealed record ImplantCertificateSubject(ImplantId ImplantId, EngagementId EngagementId);

/// <summary>
/// A leaf implant certificate and the CA chain (root first) needed to validate
/// it. All entries are DER-encoded.
/// </summary>
public sealed record IssuedCertificate(byte[] Leaf, IReadOnlyList<byte[]> CaChain);
