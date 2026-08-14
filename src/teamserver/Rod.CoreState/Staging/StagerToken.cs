namespace Rod.CoreState.Staging;

/// <summary>
/// An engagement-scoped, short-lived, bounded-use secret used only during
/// initial enrollment/deployment (glossary). The plaintext <see cref="Secret"/>
/// is returned to the caller exactly once, at mint time; the server keeps only a
/// hash so a later verify/redeem step () can check it without storing it.
/// </summary>
public sealed record StagerToken
{
    /// <summary>Server-assigned token identifier.</summary>
    public required StagerTokenId Id { get; init; }

    /// <summary>The engagement this token grants initial access to.</summary>
    public required EngagementId EngagementId { get; init; }

    /// <summary>
    /// The plaintext secret, base64url-encoded. Present only on the mint result
    /// handed back to the operator; never persisted in the clear.
    /// </summary>
    public required string Secret { get; init; }

    /// <summary>The operator who minted the token.</summary>
    public required OperatorId IssuedBy { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>Hard expiry; the token is invalid after this instant.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>How many times the token may be redeemed before it is spent.</summary>
    public required int MaxUses { get; init; }
}

/// <summary>
/// The result of a successful <see cref="IStagerTokenService.RedeemAsync"/>: the
/// engagement the redeemed token grants access to. Carries no secret -- the
/// plaintext was matched and discarded; enrollment proceeds against this
/// engagement. <see cref="IssuedBy"/> is the operator who minted the token --
/// the one who authorized the deployment -- surfaced here so enrollment can
/// attribute the resulting implant (and its later implant-initiated events) to
/// an accountable operator (architecture.md Sec 11).
/// </summary>
public sealed record RedeemedStagerToken
{
    /// <summary>The token that was redeemed.</summary>
    public required StagerTokenId Id { get; init; }

    /// <summary>The engagement this token grants initial access to.</summary>
    public required EngagementId EngagementId { get; init; }

    /// <summary>
    /// The operator who minted the redeemed token -- the authorizing deployer.
    /// Read off the stored token at redeem time so the implant-initiated
    /// enrollment that follows can attribute itself to an operator.
    /// </summary>
    public required OperatorId IssuedBy { get; init; }
}
