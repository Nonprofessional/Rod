using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Operators.Interaction;

/// <summary>
/// One interaction surface an operator can hold the claim on
/// (architecture.md Sec 4.5): the typing halves, exactly. A
/// <see cref="ChannelTask"/> is a live channel task -- its input posts and
/// relay binds, the shell and tunnel channels alike; a <see cref="ShellSession"/>
/// is a caught shell's input route.
/// </summary>
public enum InteractionSurface
{
    ChannelTask,
    ShellSession,
}

public static class InteractionSurfaceExtensions
{
    /// <summary>
    /// The route segment that names the surface on the claims API
    /// (<c>channel</c> / <c>shell</c>).
    /// </summary>
    public static string RouteKind(this InteractionSurface surface)
        => surface switch
        {
            InteractionSurface.ChannelTask => "channel",
            InteractionSurface.ShellSession => "shell",
            _ => throw new ArgumentOutOfRangeException(nameof(surface)),
        };

    /// <summary>Parses the claims API's route segment back into a surface.</summary>
    public static bool TryParseKind(string? text, out InteractionSurface surface)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "channel":
                surface = InteractionSurface.ChannelTask;
                return true;
            case "shell":
                surface = InteractionSurface.ShellSession;
                return true;
            default:
                surface = default;
                return false;
        }
    }
}

/// <summary>
/// An operator's exclusive claim on one interaction surface: which operator
/// holds it and since when. Ephemeral coordination state beside presence --
/// the audit trail's per-input events (<c>ChannelInput</c>,
/// <c>ShellSessionInput</c>) remain the attributed who-typed-what record.
/// </summary>
public sealed record InteractionClaim(
    EngagementId EngagementId,
    InteractionSurface Surface,
    Guid SurfaceId,
    OperatorId OperatorId,
    DateTimeOffset AcquiredAt);

/// <summary>
/// One per-implant driving entry of activity presence (architecture.md
/// Sec 4.5): the operator whose tasking actions an implant last saw, and
/// when. Soft state -- it marks who is working what, it does not block.
/// </summary>
public sealed record InteractionActivity(
    EngagementId EngagementId,
    ImplantId ImplantId,
    OperatorId OperatorId,
    DateTimeOffset At);

/// <summary>
/// The outcome of trying to take a claim: either the caller holds it now
/// (<paramref name="Acquired"/> with the claim, idempotent for a holder that
/// already held it), or another operator does and the refusal must name them.
/// </summary>
public readonly record struct InteractionAcquireResult(
    bool Acquired,
    InteractionClaim? Claim,
    InteractionClaim? HeldBy)
{
    public static InteractionAcquireResult Taken(InteractionClaim claim) => new(true, claim, null);

    public static InteractionAcquireResult Refused(InteractionClaim holder) => new(false, null, holder);
}
