namespace Rod.CoreState.Engagements;

/// <summary>
/// The two roles a member holds in one engagement (architecture.md Sec 3):
/// <see cref="Reader"/> is the viewing half -- every engagement-scoped read
/// and the live stream -- and <see cref="Writer"/> adds every
/// engagement-scoped write, from tasking to listeners to closeout. The
/// engagement's owner is not a member role: ownership is bound once at
/// creation and carries member management beside full write access, so the
/// role set stays the collaboration tier beneath it.
/// </summary>
public enum EngagementRole
{
    None = 0,
    Reader = 1,
    Writer = 2,
}

public static class EngagementRoles
{
    /// <summary>
    /// The wire name of the role ("reader" / "writer"), for request parsing
    /// and response rendering.
    /// </summary>
    public static string ToName(EngagementRole role)
        => role switch
        {
            EngagementRole.Reader => "reader",
            EngagementRole.Writer => "writer",
            _ => "none",
        };

    /// <summary>
    /// Parses a role name from a request. Returns false for anything but the
    /// two known names -- an unknown role is a client error, not a degrade.
    /// </summary>
    public static bool TryFromName(string? name, out EngagementRole role)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case "reader":
                role = EngagementRole.Reader;
                return true;
            case "writer":
                role = EngagementRole.Writer;
                return true;
            default:
                role = EngagementRole.None;
                return false;
        }
    }
}
