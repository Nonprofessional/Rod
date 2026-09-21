using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rod.CoreState.Pki;

/// <summary>
/// The engagement CA: the tasking signer every implant verifies its
/// dispatched work under, the root the https fronts' server leaves issue
/// from, and the chain the enrollment answer carries (architecture.md
/// Sec 9). Implant transport certificates retired with the mTLS family --
/// identity is the per-artifact contact key -- so the authority signs no
/// implant leaves in-tree; a community family reviving transport
/// certificates builds its own issuer over <see cref="GetCaCertificate"/>.
/// The default is a self-signed dev CA; production rotates to an externally
/// provisioned engagement CA without changing this contract.
/// </summary>
public interface IImplantCertificateAuthority
{
    /// <summary>
    /// The CA root certificate. The enrollment answer carries its DER as the
    /// chain (the tasking signer an implant pins), and the transport layer
    /// reads it as the root its server leaves issue from.
    /// </summary>
    X509Certificate2 GetCaCertificate();

    /// <summary>
    /// A TLS server certificate for the implant-facing listeners, signed by this
    /// CA and carrying the server-authentication usage, private key attached.
    /// SChannel (the Windows TLS stack) refuses to complete a handshake whose
    /// server certificate is not valid for server authentication, so
    /// presenting the CA's own root -- whose key usage is certificate
    /// signing only -- aborts on Windows even though Linux's OpenSSL
    /// tolerates it. The authority issues a real end-entity server leaf
    /// instead; the same certificate serves every connection for the
    /// authority's lifetime. Implant clients pin the CA and do no name
    /// matching, so the leaf carries no hostname promises.
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
