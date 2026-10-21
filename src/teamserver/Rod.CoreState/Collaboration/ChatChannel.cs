namespace Rod.CoreState.Collaboration;

/// <summary>
/// One engagement-scoped chat channel (architecture.md Sec 15.3, ADR-005):
/// a coordination room for the engagement's operators. Channels are the
/// mutable process-knowledge half beside comments -- messages ride the
/// timeline and live bus as ordinary events but never enter the audit
/// chain, and a message, once sent, is append-only (a correction is a new
/// message).
/// </summary>
public sealed class ChatChannel
{
    public ChatChannelId Id { get; }
    public EngagementId EngagementId { get; }

    /// <summary>The operator-facing name ("general", "night-watch"), unique
    /// per engagement.</summary>
    public string Name { get; }

    /// <summary>The operator who created the channel. The default channel a
    /// list call materializes attributes to the engagement's owner.</summary>
    public OperatorId CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// The full-state constructor (the persistence mirror rehydrates through
    /// it); creation goes through <see cref="Create"/>, which enforces the
    /// bounds.
    /// </summary>
    public ChatChannel(
        ChatChannelId id,
        EngagementId engagementId,
        string name,
        OperatorId createdBy,
        DateTimeOffset createdAt)
    {
        Id = id;
        EngagementId = engagementId;
        Name = name;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
    }

    /// <summary>
    /// Creates a channel, enforcing the structural bounds. Throws
    /// <see cref="ArgumentException"/> on out-of-bounds shape.
    /// </summary>
    public static ChatChannel Create(
        ChatChannelId id,
        EngagementId engagementId,
        string name,
        OperatorId createdBy,
        DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A channel needs a name.", nameof(name));
        if (name.Trim().Length > CollaborationLimits.MaxChannelNameBytes)
            throw new ArgumentException($"The channel name exceeds {CollaborationLimits.MaxChannelNameBytes} bytes.");
        return new ChatChannel(id, engagementId, name.Trim(), createdBy, createdAt);
    }
}

/// <summary>
/// The refusable-channel signal the collaboration service throws: a channel
/// the structural bounds did not catch -- a duplicate name, the per-
/// engagement channel budget, or a closed engagement. The endpoint maps it
/// to 422.
/// </summary>
public sealed class ChatRejectedException : DomainException
{
    public ChatRejectedException(string message)
        : base(message)
    {
    }
}
