using System.Security.Cryptography;
using System.Text;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.CoreState.Staging;

namespace Rod.CoreState.Application;

/// <summary>
/// The enrollment use case (): a stager token is redeemed to bind a
/// new implant to an engagement, the implant is recorded with a server-generated
/// per-implant key and kill date, and the CA issues a certificate binding
/// <c>(implant_id, engagement_id)</c> (architecture.md Sec 9). Orchestrates the
/// core-state ports; holds no state of its own. Redeem failures propagate as
/// <see cref="StagerTokenRedeemException"/> so the transport endpoint can map
/// them to wire status codes without the core depending on the wire protocol.
/// </summary>
public sealed class EnrollmentService
{
    // Skeleton defaults; these become per-request / profile inputs later
    // (kill date is an concern; the value here only sets the recorded shape).
    private static readonly TimeSpan DefaultKillDateOffset = TimeSpan.FromDays(30);

    private readonly IEngagementRepository _engagements;
    private readonly IStagerTokenService _stagerTokens;
    private readonly IImplantRepository _implants;
    private readonly IImplantCertificateAuthority _certificateAuthority;
    private readonly TimeProvider _clock;

    public EnrollmentService(
        IEngagementRepository engagements,
        IStagerTokenService stagerTokens,
        IImplantRepository implants,
        IImplantCertificateAuthority certificateAuthority,
        TimeProvider clock)
    {
        _engagements = engagements;
        _stagerTokens = stagerTokens;
        _implants = implants;
        _certificateAuthority = certificateAuthority;
        _clock = clock;
    }

    /// <summary>
    /// Redeems the presented stager token, enrolls a new implant into the token's
    /// engagement, and issues its bound certificate. Throws
    /// <see cref="StagerTokenRedeemException"/> when the token is unknown, expired,
    /// or spent -- the caller maps that to a wire status.
    ///
    /// When <see cref="EnrollCommand.ClientPublicKey"/> is present the leaf is
    /// issued over the implant's own public key, so the implant keeps its private
    /// key and can present the certificate in mTLS (architecture.md Sec 9). This is
    /// the path a real implant takes: it generates its key pair and sends only the
    /// public half. When absent, the CA generates an ephemeral leaf key (the
    /// original  shape, kept for back-compat with tests that do not need an
    /// mTLS-capable identity).
    ///
    /// When <see cref="EnrollCommand.ParentImplantId"/> is set the implant is a
    /// child derived from that parent (architecture.md Sec 5.2, ): the
    /// parent must exist, belong to the same engagement the token redeemed, and not
    /// be retired, or a <see cref="InvalidParentImplantException"/> is thrown for
    /// the caller to map to a wire status. The child enrols into the redeemed
    /// engagement and records its parent; a null parent enrolls a top-level implant.
    /// </summary>
    public async Task<EnrollmentResult> EnrollAsync(
        EnrollCommand command,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // 1. Redeem the token -- consumes one use and resolves the engagement.
        //    Throws StagerTokenRedeemException on failure; let it propagate.
        var redeemed = await _stagerTokens.RedeemAsync(command.StagerTokenSecret, now, cancellationToken);

        // 2. Confirm the engagement still exists (it may have been torn down).
        await _engagements.GetOrThrowAsync(redeemed.EngagementId, cancellationToken);

        // 3. Resolve and scope-check the parent when this is a child enrollment
        //    (architecture.md Sec 5.2). The child enrols into the parent's
        //    engagement -- which must equal the redeemed engagement, so a child
        //    cannot be grafted across engagements -- and the parent must be live.
        //    Throws InvalidParentImplantException on failure; let it propagate.
        var parent = command.ParentImplantId is { } parentId
            ? await ResolveParentAsync(parentId, redeemed.EngagementId, cancellationToken)
            : null;

        // 4. Build the implant: server-generated per-implant key + default kill date.
        //    EnrollChild records the parent when present; a null parent yields the
        //    top-level shape, so the two paths share one factory. The implant's
        //    DeployedBy is the operator who minted the redeemed token -- the one
        //    who authorized this deployment -- so the implant-initiated events that
        //    follow (a session opening, tasking) attribute to an accountable
        //    operator (architecture.md Sec 11).
        var implantId = ImplantId.New();
        var key = Base64Url(RandomNumberGenerator.GetBytes(32));
        var killDate = now + DefaultKillDateOffset;
        var implant = Implant.EnrollChild(implantId, redeemed.EngagementId, key, killDate, command.Class, now, redeemed.IssuedBy, parent?.Id);
        await _implants.SaveAsync(implant, cancellationToken);

        // 5. Issue the certificate bound to (implant_id, engagement_id). Over the
        //    implant's own public key when it supplied one (the mTLS-capable path);
        //    over a server-generated ephemeral key otherwise.
        var subject = new ImplantCertificateSubject(implant.Id, redeemed.EngagementId);
        var issued = command.ClientPublicKey is { } publicKeyDer
            ? await IssueOverClientPublicKeyAsync(subject, publicKeyDer, cancellationToken)
            : await _certificateAuthority.IssueAsync(subject, cancellationToken);

        return new EnrollmentResult(
            implant.Id,
            redeemed.EngagementId,
            key,
            killDate,
            command.Class,
            issued.Leaf,
            issued.CaChain,
            implant.DeployedBy,
            implant.ParentImplantId,
            now);
    }

    // Resolves the parent implant and enforces the engagement-scope and liveness
    // rules a child derivation requires (architecture.md Sec 5.2/3). The parent
    // must exist, belong to the same engagement the child's token redeemed, and not
    // be retired; each failure throws InvalidParentImplantException with a distinct
    // reason the transport maps to a wire status. Centralized so the enroll path
    // has one place that defines "a valid parent".
    private async Task<Implant> ResolveParentAsync(
        ImplantId parentId,
        EngagementId engagementId,
        CancellationToken cancellationToken)
    {
        var parent = await _implants.FindAsync(parentId, cancellationToken);
        if (parent is null)
        {
            throw new InvalidParentImplantException(
                InvalidParentImplantReason.Unknown,
                $"Parent implant {parentId} is not enrolled.");
        }
        if (parent.EngagementId != engagementId)
        {
            // A child enrols into the same engagement as its parent
            // (architecture.md Sec 3); a parent in another engagement is refused
            // without revealing that the implant exists elsewhere.
            throw new InvalidParentImplantException(
                InvalidParentImplantReason.EngagementMismatch,
                $"Parent implant {parentId} is not in engagement {engagementId}.");
        }
        if (parent.IsRetired)
        {
            throw new InvalidParentImplantException(
                InvalidParentImplantReason.Retired,
                $"Parent implant {parentId} was retired and cannot derive children.");
        }
        return parent;
    }

    // Decodes the implant-supplied public key (DER SubjectPublicKeyInfo) and asks the
    // CA to sign a leaf over it. RSA is the only key type the dev CA and the current
    // implant set speak; anything else is a malformed request, mapped to BadToken by
    // the transport endpoint.
    private Task<IssuedCertificate> IssueOverClientPublicKeyAsync(
        ImplantCertificateSubject subject,
        byte[] publicKeyDer,
        CancellationToken cancellationToken)
    {
        using var publicKey = RSA.Create();
        publicKey.ImportSubjectPublicKeyInfo(publicKeyDer, out _);
        return _certificateAuthority.IssueWithPublicKeyAsync(subject, publicKey, cancellationToken);
    }

    // RFC 4648 base64url without padding -- URL-safe, matches the stager-token encoding.
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}

/// <summary>
/// Request to enroll an implant. The stager token secret resolves the
/// engagement; <see cref="Class"/> defaults to a stage-2 implant. When
/// <see cref="ClientPublicKey"/> is set it is a DER SubjectPublicKeyInfo the CA
/// signs a leaf over, so the implant keeps its private key for mTLS
/// (architecture.md Sec 9); null leaves the CA to generate an ephemeral leaf key.
///
/// <see cref="ParentImplantId"/> () derives a child implant: when set,
/// the service resolves and scope-checks the parent before recording the child.
/// Null (the default) enrolls a top-level implant from the stager token.
/// </summary>
public sealed record EnrollCommand(
    string StagerTokenSecret,
    ImplantClass Class = ImplantClass.Stage2,
    byte[]? ClientPublicKey = null,
    ImplantId? ParentImplantId = null);

/// <summary>
/// Result of a successful enrollment: the new implant's identity, its engagement,
/// its per-implant key (shown once, as with the stager secret), the recorded kill
/// date, the bound certificate plus CA chain, the operator who deployed it (the
/// token issuer, used to attribute the enrollment), the parent it was derived
/// from (null for a top-level implant), and the enrollment timestamp.
/// </summary>
public sealed record EnrollmentResult(
    ImplantId ImplantId,
    EngagementId EngagementId,
    string Key,
    DateTimeOffset KillDate,
    ImplantClass Class,
    byte[] LeafCertificate,
    IReadOnlyList<byte[]> CaChain,
    OperatorId DeployedBy,
    ImplantId? ParentImplantId,
    DateTimeOffset EnrolledAt);
