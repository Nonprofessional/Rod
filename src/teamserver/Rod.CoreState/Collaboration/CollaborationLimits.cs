namespace Rod.CoreState.Collaboration;

/// <summary>
/// The collaboration surface's standing boundaries (architecture.md
/// Sec 15.3, ADR-005). Domain rules, not console knobs -- every comment and
/// chat message passes them at publish.
/// </summary>
public static class CollaborationLimits
{
    /// <summary>
    /// Bounds one comment's body, the same ceiling an operator note carries:
    /// process knowledge is a paragraph, not a report.
    /// </summary>
    public const int MaxCommentChars = 8 * 1024;

    /// <summary>
    /// Bounds one chat message: the channel is quick coordination, and a
    /// longer text is a comment on the entity it concerns.
    /// </summary>
    public const int MaxChatMessageChars = 4 * 1024;

    /// <summary>Bounds a chat channel's display name.</summary>
    public const int MaxChannelNameBytes = 200;

    /// <summary>
    /// How many chat channels one engagement may hold. Channels are the
    /// watch's rooms -- an engagement needing more has a coordination
    /// problem, and the general channel already exists.
    /// </summary>
    public const int MaxChannelsPerEngagement = 20;

    /// <summary>
    /// How many rows one credential-kind task's output may land in the
    /// credential store -- the same ceiling the read-time projection parsed
    /// against, so a runaway harvest stays bounded.
    /// </summary>
    public const int MaxCredentialRowsPerTask = 10_000;
}
