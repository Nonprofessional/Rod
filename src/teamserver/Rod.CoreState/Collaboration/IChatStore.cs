namespace Rod.CoreState.Collaboration;

/// <summary>
/// The durable home for engagement-scoped chat: the channels and their
/// messages (architecture.md Sec 15.3, ADR-005). Chat is mutable process
/// knowledge beside comments -- messages ride the timeline and the live bus
/// as ordinary events but never enter the audit chain. In-memory by
/// default, Postgres-backed when the connection string is set.
/// </summary>
public interface IChatStore
{
    /// <summary>Saves a channel (the id is the identity).</summary>
    Task SaveChannelAsync(ChatChannel channel, CancellationToken cancellationToken = default);

    /// <summary>The channel, or null when unknown.</summary>
    Task<ChatChannel?> FindChannelAsync(ChatChannelId id, CancellationToken cancellationToken = default);

    /// <summary>The engagement's channels, oldest first.</summary>
    Task<IReadOnlyList<ChatChannel>> ListChannelsAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default);

    /// <summary>Saves a message (messages are append-only).</summary>
    Task SaveMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// One newest-window page of one channel's messages, oldest first
    /// within the page -- the panel read.
    /// </summary>
    Task<NewestWindowPage<ChatMessage>> ListPageByChannelAsync(
        ChatChannelId channelId,
        int limit,
        DateTimeOffset? before,
        Guid? beforeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One newest-window page of the engagement's messages across channels,
    /// oldest first within the page -- the timeline read, keyed on
    /// (SentAt, Id) with the same keyset the audit listing pages on.
    /// </summary>
    Task<NewestWindowPage<ChatMessage>> ListPageByEngagementAsync(
        EngagementId engagementId,
        int limit,
        DateTimeOffset? before,
        Guid? beforeId,
        CancellationToken cancellationToken = default);
}
