namespace Rod.CoreState.Staging;

/// <summary>
/// Mints and redeems stager tokens. The mint result carries the plaintext secret
/// exactly once; only a hash is retained server-side so a stolen store cannot
/// replay tokens. Redeem is the entry point of enrollment: a presenting
/// stager is verified against the stored hash, checked for expiry and remaining
/// uses, and consumed on success. <see cref="VerifyAsync"/> is the same check
/// without the consume -- the pre-enrollment read a stage-1 stager's payload
/// fetch performs (architecture.md Sec 6): the fetch may not spend the
/// deployment credential, because the stage-2 it launches spends it at enroll.
/// </summary>
public interface IStagerTokenService
{
    /// <summary>
    /// Mints a fresh stager token for <paramref name="engagementId"/>, issued by
    /// <paramref name="issuedBy"/>. The returned secret is shown once.
    /// </summary>
    Task<StagerToken> MintAsync(
        EngagementId engagementId,
        OperatorId issuedBy,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a stager token by its plaintext <paramref name="secret"/> at
    /// <paramref name="now"/>. Verifies the hash without ever storing the clear
    /// secret, refuses expired or spent tokens, and consumes one use on success.
    /// Throws <see cref="StagerTokenRedeemException"/> with a
    /// <see cref="StagerTokenRedeemReason"/> the caller (the enroll endpoint) maps
    /// to a wire status code.
    /// </summary>
    Task<RedeemedStagerToken> RedeemAsync(
        string secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a stager token by its plaintext <paramref name="secret"/> at
    /// <paramref name="now"/> without consuming a use: the same hash, expiry,
    /// and remaining-uses checks as <see cref="RedeemAsync"/>, the same refusal
    /// exceptions, but the token's state is untouched so the enrollment that
    /// follows can still spend it.
    /// </summary>
    Task<RedeemedStagerToken> VerifyAsync(
        string secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
