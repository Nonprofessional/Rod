namespace Rod.Operators.Collaboration;

/// <summary>
/// The collaboration surface's runtime knobs (architecture.md Sec 15.3,
/// ADR-005). The one knob that matters is the comment edit window: how long
/// after creation the author may still edit or delete. Domain bounds live
/// on <c>CollaborationLimits</c>; this is deployment configuration.
/// </summary>
public sealed class CollaborationOptions
{
    public const string SectionName = "Collaboration";

    /// <summary>
    /// How many minutes after creation a comment stays editable by its
    /// author. One long watch plus the handoff gap -- the period in which a
    /// correction is still the author's to make. After it, the comment is
    /// read-only process knowledge until the engagement ends.
    /// </summary>
    public int CommentEditWindowMinutes { get; set; } = 24 * 60;

    /// <summary>The window as a timespan.</summary>
    public TimeSpan CommentEditWindow
        => TimeSpan.FromMinutes(Math.Max(0, CommentEditWindowMinutes));
}
