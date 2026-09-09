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
    /// <paramref name="issuedBy"/>. The returned secret is shown once. The
    /// optional <paramref name="maxUses"/> and <paramref name="lifetime"/> scope
    /// the token to a deployment batch: absent values keep the single-use,
    /// one-hour default; a batch of N implants mints one token with N uses and
    /// a longer window, each enroll spending one use.
    /// </summary>
    Task<StagerToken> MintAsync(
        EngagementId engagementId,
        OperatorId issuedBy,
        DateTimeOffset issuedAt,
        int? maxUses = null,
        TimeSpan? lifetime = null,
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

    /// <summary>
    /// Deletes a token by id: the emergency answer to a leaked credential,
    /// especially one baked into a deployed artifact. The next redeem (or
    /// verify) of its secret reads Unknown. Returns false when the id is not
    /// stored (already revoked, spent and removed, or never minted).
    /// </summary>
    Task<bool> RevokeAsync(
        StagerTokenId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the token's state by id -- the budget and window an operator
    /// minted, without the secret or any consume. The payload library joins
    /// this onto its rows so an operator can see how many enrolls a deployed
    /// artifact's credential has left. Null when the id is not stored: revoked,
    /// spent-and-removed (the in-memory store drops a token at zero), or never
    /// minted.
    /// </summary>
    Task<StagerTokenState?> FindAsync(
        StagerTokenId id,
        CancellationToken cancellationToken = default);
}
