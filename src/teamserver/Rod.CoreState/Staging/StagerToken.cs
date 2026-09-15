namespace Rod.CoreState.Staging;

/// <summary>
/// An engagement-scoped, short-lived, bounded-use secret used only during
/// initial enrollment/deployment (glossary). The plaintext <see cref="Secret"/>
/// is returned to the caller exactly once, at mint time; the server keeps only a
/// hash so a later verify/redeem step can check it without storing it.
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

    /// <summary>
    /// How many times the token may be redeemed before it is spent. Zero means
    /// unlimited: the token never spends down and stays redeemable until it
    /// expires or is revoked.
    /// </summary>
    public required int MaxUses { get; init; }

    /// <summary>
    /// The caught shell session an upgrade render minted this token for, when
    /// it did (architecture.md Sec 8): the enrollment that redeems the token
    /// binds the new implant back to the shell it grew out of. Null on every
    /// other mint -- the field is provenance, never authority.
    /// </summary>
    public ShellSessionId? OriginShellSession { get; init; }
}

/// <summary>
/// The token's inspectable state, read by id: the budget an operator minted
/// (max uses, how many are left) and the window it lives in. No secret rides
/// here -- this is the ledger side of a credential whose plaintext exists only
/// inside the artifact it was baked into. A spent token's state depends on the
/// store: the durable store keeps the row at zero remaining uses, the
/// in-memory one drops it (so a missing read there means spent, revoked, or
/// never minted).
/// </summary>
public sealed record StagerTokenState
{
    /// <summary>The token this state describes.</summary>
    public required StagerTokenId Id { get; init; }

    /// <summary>The engagement the token grants access to.</summary>
    public required EngagementId EngagementId { get; init; }

    /// <summary>The operator who minted the token.</summary>
    public required OperatorId IssuedBy { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>Hard expiry; the token is invalid after this instant.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// How many times the token may be redeemed in total. Zero means unlimited.
    /// </summary>
    public required int MaxUses { get; init; }

    /// <summary>
    /// How many redeems remain before the token is spent. Zero on an unlimited
    /// budget (<see cref="MaxUses"/> == 0) means "not counted", not "spent".
    /// </summary>
    public required int RemainingUses { get; init; }
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
    /// The caught shell session the minted token's upgrade render belongs to,
    /// when it does (architecture.md Sec 8): the enrollment binds the new
    /// implant back to the shell it grew out of.
    /// </summary>
    public ShellSessionId? OriginShellSession { get; init; }

    /// <summary>
    /// The operator who minted the redeemed token -- the authorizing deployer.
    /// Read off the stored token at redeem time so the implant-initiated
    /// enrollment that follows can attribute itself to an operator.
    /// </summary>
    public required OperatorId IssuedBy { get; init; }
}
