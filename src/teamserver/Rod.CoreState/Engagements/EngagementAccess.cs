namespace Rod.CoreState.Engagements;

/// <summary>
/// One operator's standing toward one engagement (architecture.md Sec 3):
/// what the access check resolves to, not how it got there. The owner is a
/// level of its own -- ownership carries member management beside full write
/// access -- and a stranger resolves to <see cref="None"/> with the
/// engagement's existence carried separately, so a caller can render "not
/// found" for both a missing engagement and a hidden one, indistinguishably.
/// </summary>
public enum EngagementAccessLevel
{
    /// <summary>The operator holds no standing -- not owner, not member.</summary>
    None = 0,

    /// <summary>A member holding the viewing half: reads and the live stream.</summary>
    Reader = 1,

    /// <summary>A member holding the acting half: every engagement-scoped write.</summary>
    Writer = 2,

    /// <summary>The engagement's owner: writes plus member management. Bound
    /// once at creation; not a membership and not grantable.</summary>
    Owner = 3,
}

/// <summary>
/// The resolved answer to "what may this operator do on this engagement":
/// whether the engagement exists at all (a missing engagement and a hidden
/// one render the same), the level the operator holds, and (when it exists)
/// the owner the engagement records -- so callers can target owner-specific
/// rules without a second lookup.
/// </summary>
public sealed record EngagementAccess(
    bool EngagementExists,
    EngagementAccessLevel Level,
    OperatorId? OwnerId = null)
{
    public static readonly EngagementAccess Missing = new(false, EngagementAccessLevel.None);

    /// <summary>May enter the engagement's read surface (any member or the owner).</summary>
    public bool CanRead
        => EngagementExists && Level >= EngagementAccessLevel.Reader;

    /// <summary>May exercise the engagement's write surface (writer or owner).</summary>
    public bool CanWrite
        => EngagementExists && Level >= EngagementAccessLevel.Writer;
}
