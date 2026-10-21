namespace Rod.CoreState.Collaboration;

/// <summary>
/// One message on an engagement chat channel (architecture.md Sec 15.3,
/// ADR-005). Append-only by design: the channel is live coordination, a
/// correction is a new message, and the stored body is what was sent.
/// Task-reference tokens (<c>@task-&lt;id&gt;</c>, <c>$cred-&lt;id&gt;</c>)
/// stay in placeholder form -- references resolve at publish, and a
/// credential's secret never lands in a chat row.
/// </summary>
public sealed class ChatMessage
{
    public ChatMessageId Id { get; }
    public EngagementId EngagementId { get; }
    public ChatChannelId ChannelId { get; }

    /// <summary>The operator who sent the message.</summary>
    public OperatorId AuthorId { get; }

    public string Body { get; }

    public DateTimeOffset SentAt { get; }

    /// <summary>
    /// The full-state constructor (the persistence mirror rehydrates through
    /// it); creation goes through <see cref="Create"/>, which enforces the
    /// bounds.
    /// </summary>
    public ChatMessage(
        ChatMessageId id,
        EngagementId engagementId,
        ChatChannelId channelId,
        OperatorId authorId,
        string body,
        DateTimeOffset sentAt)
    {
        Id = id;
        EngagementId = engagementId;
        ChannelId = channelId;
        AuthorId = authorId;
        Body = body;
        SentAt = sentAt;
    }

    /// <summary>
    /// Creates a message, enforcing the structural bounds. Throws
    /// <see cref="ArgumentException"/> on out-of-bounds shape.
    /// </summary>
    public static ChatMessage Create(
        ChatMessageId id,
        EngagementId engagementId,
        ChatChannelId channelId,
        OperatorId authorId,
        string body,
        DateTimeOffset sentAt)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("A chat message needs a body.", nameof(body));
        if (body.Length > CollaborationLimits.MaxChatMessageChars)
            throw new ArgumentException($"The chat message exceeds {CollaborationLimits.MaxChatMessageChars} characters.");
        return new ChatMessage(id, engagementId, channelId, authorId, body.Trim(), sentAt);
    }
}
