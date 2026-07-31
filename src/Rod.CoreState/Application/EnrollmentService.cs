using System.Security.Cryptography;
using System.Text;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.CoreState.Staging;

namespace Rod.CoreState.Application;

/// <summary>
/// The enrollment use case (roadmap M1.2): a stager token is redeemed to bind a
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
    // (kill date is an M4.2 concern; the value here only sets the recorded shape).
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

        // 3. Build the implant: server-generated per-implant key + default kill date.
        var implantId = ImplantId.New();
        var key = Base64Url(RandomNumberGenerator.GetBytes(32));
        var killDate = now + DefaultKillDateOffset;
        var implant = Implant.Enroll(implantId, redeemed.EngagementId, key, killDate, command.Class, now);
        await _implants.SaveAsync(implant, cancellationToken);

        // 4. Issue the certificate bound to (implant_id, engagement_id).
        var issued = await _certificateAuthority.IssueAsync(
            new ImplantCertificateSubject(implant.Id, redeemed.EngagementId), cancellationToken);

        return new EnrollmentResult(
            implant.Id,
            redeemed.EngagementId,
            key,
            killDate,
            command.Class,
            issued.Leaf,
            issued.CaChain);
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
/// engagement; <see cref="Class"/> defaults to a stage-2 implant.
/// </summary>
public sealed record EnrollCommand(string StagerTokenSecret, ImplantClass Class = ImplantClass.Stage2);

/// <summary>
/// Result of a successful enrollment: the new implant's identity, its engagement,
/// its per-implant key (shown once, as with the stager secret), the recorded kill
/// date, and the bound certificate plus CA chain.
/// </summary>
public sealed record EnrollmentResult(
    ImplantId ImplantId,
    EngagementId EngagementId,
    string Key,
    DateTimeOffset KillDate,
    ImplantClass Class,
    byte[] LeafCertificate,
    IReadOnlyList<byte[]> CaChain);
