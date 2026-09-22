namespace Rod.CoreState.Deployment;

/// <summary>
/// Mints and redeems deploy tokens. The mint result carries the plaintext secret
/// exactly once; only a hash is retained server-side so a stolen store cannot
/// replay tokens. Redeem is the consuming read: a presenting downloader is
/// verified against the stored hash, checked for expiry, remaining uses, and
/// revocation, and one use is spent on success -- an enrollment spends the
/// artifact's baked credential this way, and a served payload fetch spends the
/// launcher's the same way. <see cref="VerifyAsync"/> is the non-consuming twin
/// every refusal path resolves through first, so a budget is only ever spent
/// on an action that actually serves the presenter.
/// </summary>
public interface IDeployTokenService
{
    /// <summary>
    /// Mints a fresh deploy token for <paramref name="engagementId"/>, issued by
    /// <paramref name="issuedBy"/>. The returned secret is shown once. The
    /// optional <paramref name="maxUses"/> and <paramref name="lifetime"/> scope
    /// the token to a deployment batch: absent values keep the single-use,
    /// one-hour default; a batch of N implants mints one token with N uses and
    /// a longer window, each enroll spending one use.
    /// </summary>
    Task<DeployToken> MintAsync(
        EngagementId engagementId,
        OperatorId issuedBy,
        DateTimeOffset issuedAt,
        int? maxUses = null,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a deploy token by its plaintext <paramref name="secret"/> at
    /// <paramref name="now"/>. Verifies the hash without ever storing the clear
    /// secret, refuses expired, spent, or revoked tokens, and consumes one use
    /// on success. Throws <see cref="DeployTokenRedeemException"/> with a
    /// <see cref="DeployTokenRedeemReason"/> (and the resolved token's
    /// attribution, when the secret matched) the caller maps to a wire status
    /// code or records on the audit trail.
    /// </summary>
    Task<RedeemedDeployToken> RedeemAsync(
        string secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a deploy token by its plaintext <paramref name="secret"/> at
    /// <paramref name="now"/> without consuming a use: the same hash, expiry,
    /// remaining-uses, and revocation checks as <see cref="RedeemAsync"/>, the
    /// same refusal exceptions, but the token's state is untouched. The
    /// resolution step a refusal path takes before it decides to serve -- and
    /// spending belongs to <see cref="RedeemAsync"/> alone.
    /// </summary>
    Task<RedeemedDeployToken> VerifyAsync(
        string secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a token by id: the emergency answer to a leaked credential,
    /// especially one baked into a deployed artifact. A soft kill -- the row
    /// stays resolvable with its revocation stamp, so later attempts on the
    /// secret read <see cref="DeployTokenRedeemReason.Revoked"/> with their
    /// engagement attribution instead of dissolving into Unknown. Returns true
    /// only on the flip; false when the id is not stored or already revoked,
    /// so a repeat revoke neither reads as success nor double-writes.
    /// </summary>
    Task<bool> RevokeAsync(
        DeployTokenId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes a token by id: the resolution itself goes, so the next
    /// redeem (or verify) of its secret reads Unknown and belongs to no
    /// engagement. The kill that rides a launcher row's deletion -- once the
    /// row is gone, the credential's attempts must leave no record. Returns
    /// false when the id is not stored.
    /// </summary>
    Task<bool> DeleteAsync(
        DeployTokenId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the token's state by id -- the budget and window an operator
    /// minted, without the secret or any consume. The payload library joins
    /// this onto its rows so an operator can see how many enrolls a deployed
    /// artifact's credential has left. Null when the id is not stored, or the
    /// credential is revoked (the launcher row carries its own revocation
    /// stamp); a spent budgeted token stays visible at zero remaining.
    /// </summary>
    Task<DeployTokenState?> FindAsync(
        DeployTokenId id,
        CancellationToken cancellationToken = default);
}
