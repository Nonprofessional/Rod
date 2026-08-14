namespace Rod.CoreState.Staging;

/// <summary>
/// Mints and redeems stager tokens. The mint result carries the plaintext secret
/// exactly once; only a hash is retained server-side so a stolen store cannot
/// replay tokens. Redeem () is the entry point of enrollment: a presenting
/// stager is verified against the stored hash, checked for expiry and remaining
/// uses, and consumed on success.
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
}
