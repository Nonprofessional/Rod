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
    /// Certificates are DER-encoded.
    /// </summary>
    Task<IssuedCertificate> IssueAsync(
        ImplantCertificateSubject subject,
        CancellationToken cancellationToken = default);
}

/// <summary>The identity to bind into an issued implant certificate.</summary>
public sealed record ImplantCertificateSubject(ImplantId ImplantId, EngagementId EngagementId);

/// <summary>
/// A leaf implant certificate and the CA chain (root first) needed to validate
/// it. All entries are DER-encoded.
/// </summary>
public sealed record IssuedCertificate(byte[] Leaf, IReadOnlyList<byte[]> CaChain);
