using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rod.CoreState.Pki;

/// <summary>
/// Issues the client certificate that binds an implant to its engagement
/// (architecture.md Sec 9 -- mTLS; an implant certificate binds
/// <c>(implant_id, engagement_id)</c>). Implant leaves carry ECDSA P-256 keys:
/// the implant's first-run keygen is effectively instantaneous where RSA-2048
/// costs ~100ms on-target, and an EC leaf is the smaller certificate on the
/// wire. The CA's own signing key is a separate concern and stays RSA. The
/// default is a self-signed dev CA; production rotates to an externally
/// provisioned engagement CA without changing this contract.
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
        ECDsa leafPrivateKey,
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
    /// implementation reads its curve and public point and never requires, nor
    /// sees, the private key. Both an implant enrolling over the wire and a test
    /// harness driving enrollment through the same port end here.
    /// </remarks>
    Task<IssuedCertificate> IssueWithPublicKeyAsync(
        ImplantCertificateSubject subject,
        ECDsa leafPublicKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The CA root certificate, DER-encoded. Held out so the transport layer can
    /// trust it when terminating mTLS (architecture.md Sec 9): a presenting
    /// client certificate is accepted only when it chains to this root.
    /// </summary>
    X509Certificate2 GetCaCertificate();

    /// <summary>
    /// A TLS server certificate for the implant-facing listeners, signed by this
    /// CA and carrying the server-authentication usage, private key attached.
    /// SChannel (the Windows TLS stack the .NET implant rides) refuses to
    /// complete a handshake whose server certificate is not valid for server
    /// authentication, so presenting the CA's own root -- whose key usage is
    /// certificate signing only -- aborts the mTLS beacon on Windows even
    /// though Linux's OpenSSL tolerates it. The authority issues a real
    /// end-entity server leaf instead; the same certificate serves every
    /// connection for the authority's lifetime. Implant clients pin the CA and
    /// do no name matching, so the leaf carries no hostname promises.
    /// </summary>
    X509Certificate2 GetServerCertificate();

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
    /// A non-null <paramref name="nonce"/> (the replay-nonce arm,
    /// architecture.md Sec 9) appends the nonce to the canonical tuple, so the
    /// signature covers it exactly as the negotiating implant verifies.
    /// </summary>
    byte[] SignTasking(string implantId, string taskId, string verb, string arguments, ulong? nonce = null);
}

/// <summary>The identity to bind into an issued implant certificate.</summary>
public sealed record ImplantCertificateSubject(ImplantId ImplantId, EngagementId EngagementId);

/// <summary>
/// A leaf implant certificate and the CA chain (root first) needed to validate
/// it. All entries are DER-encoded.
/// </summary>
public sealed record IssuedCertificate(byte[] Leaf, IReadOnlyList<byte[]> CaChain);
