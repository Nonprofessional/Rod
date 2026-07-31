namespace Rod.CoreState.Staging;

/// <summary>
/// An engagement-scoped, short-lived, bounded-use secret used only during
/// initial enrollment/deployment (glossary). The plaintext <see cref="Secret"/>
/// is returned to the caller exactly once, at mint time; the server keeps only a
/// hash so a later verify/redeem step (M1.2) can check it without storing it.
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
/// engagement.
/// </summary>
public sealed record RedeemedStagerToken
{
    /// <summary>The token that was redeemed.</summary>
    public required StagerTokenId Id { get; init; }

    /// <summary>The engagement this token grants initial access to.</summary>
    public required EngagementId EngagementId { get; init; }
}
