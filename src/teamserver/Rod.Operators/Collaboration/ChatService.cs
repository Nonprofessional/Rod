using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.CoreState.Engagements;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;

namespace Rod.Operators.Collaboration;

/// <summary>
/// The chat use cases (architecture.md Sec 15.3, ADR-005): the engagement's
/// coordination rooms. One default channel exists from the first read --
/// attributed to the engagement's owner, so the room is never a cold start
/// -- and writers may open more within the budget. Messages are
/// append-only: a correction is a new message, not a rewrite, and nothing
/// here writes the audit trail -- chat is mutable process knowledge, and
/// it rides the live bus and the timeline, never the chain.
/// </summary>
public sealed class ChatService
{
    /// <summary>The name of the channel every engagement carries.</summary>
    public const string DefaultChannelName = "general";

    private readonly IChatStore _chat;
    private readonly IEngagementRepository _engagements;
    private readonly TaskReferenceService _references;
    private readonly ILiveEventBus? _bus;
    private readonly TimeProvider _clock;

    public ChatService(
        IChatStore chat,
        IEngagementRepository engagements,
        TaskReferenceService references,
        TimeProvider clock,
        ILiveEventBus? bus = null)
    {
        _chat = chat;
        _engagements = engagements;
        _references = references;
        _clock = clock;
        _bus = bus;
    }

    /// <summary>
    /// The engagement's channels, oldest first -- materializing the default
    /// channel when the engagement holds none yet, so the first operator to
    /// open chat finds the room already there.
    /// </summary>
    public async Task<IReadOnlyList<ChatChannel>> ListChannelsAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        var channels = await _chat.ListChannelsAsync(engagement, cancellationToken);
        if (channels.Count > 0)
            return channels;

        var found = await _engagements.FindAsync(engagement, cancellationToken)
            ?? throw new ChatRejectedException("Unknown engagement.");
        var created = ChatChannel.Create(
            ChatChannelId.New(),
            engagement,
            DefaultChannelName,
            found.OwnerId,
            _clock.GetUtcNow());
        await _chat.SaveChannelAsync(created, cancellationToken);
        if (_bus is not null)
        {
            await _bus.PublishAsync(
                LiveEvent.Chat(engagement, LiveEventKind.ChatChannelCreated, created.CreatedBy, created.Name, created.CreatedAt),
                cancellationToken);
        }

        return [created];
    }

    public async Task<ChatChannel> CreateChannelAsync(
        EngagementId engagement,
        string name,
        OperatorId createdBy,
        CancellationToken cancellationToken = default)
    {
        var found = await _engagements.FindAsync(engagement, cancellationToken)
            ?? throw new ChatRejectedException("Unknown engagement.");
        if (found.IsClosed || found.IsRetired)
            throw new ChatRejectedException("The engagement is closed; its chat is read-only.");

        var existing = await _chat.ListChannelsAsync(engagement, cancellationToken);
        if (existing.Count >= CollaborationLimits.MaxChannelsPerEngagement)
            throw new ChatRejectedException(
                $"The engagement already holds {existing.Count} channels (limit {CollaborationLimits.MaxChannelsPerEngagement}).");
        if (existing.Any(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ChatRejectedException($"A channel named '{name.Trim()}' already exists in this engagement.");

        var channel = ChatChannel.Create(ChatChannelId.New(), engagement, name, createdBy, _clock.GetUtcNow());
        await _chat.SaveChannelAsync(channel, cancellationToken);
        if (_bus is not null)
        {
            await _bus.PublishAsync(
                LiveEvent.Chat(engagement, LiveEventKind.ChatChannelCreated, createdBy, channel.Name, channel.CreatedAt),
                cancellationToken);
        }

        return channel;
    }

    /// <summary>The channel when it belongs to the engagement, else null.</summary>
    public async Task<ChatChannel?> FindChannelAsync(
        EngagementId engagement,
        ChatChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        var channel = await _chat.FindChannelAsync(channelId, cancellationToken);
        return channel?.EngagementId == engagement ? channel : null;
    }

    /// <summary>Sends one message. The channel must already resolve to the
    /// engagement (see <see cref="FindChannelAsync"/>); the service judges
    /// the rest -- the engagement open, references resolving, the body in
    /// bounds.</summary>
    public async Task<ChatMessage> SendAsync(
        EngagementId engagement,
        ChatChannel channel,
        OperatorId author,
        string body,
        CancellationToken cancellationToken = default)
    {
        var found = await _engagements.FindAsync(engagement, cancellationToken)
            ?? throw new ChatRejectedException("Unknown engagement.");
        if (found.IsClosed || found.IsRetired)
            throw new ChatRejectedException("The engagement is closed; its chat is read-only.");

        var unresolved = await _references.UnresolvedAsync(engagement, body, cancellationToken);
        if (unresolved.Count > 0)
        {
            throw new ChatRejectedException(
                "The message references "
                + string.Join(", ", unresolved.Select(t => t.Render()))
                + " -- no such task or credential in this engagement.");
        }

        var message = ChatMessage.Create(ChatMessageId.New(), engagement, channel.Id, author, body, _clock.GetUtcNow());
        await _chat.SaveMessageAsync(message, cancellationToken);
        if (_bus is not null)
        {
            await _bus.PublishAsync(
                LiveEvent.Chat(
                    engagement,
                    LiveEventKind.ChatMessageSent,
                    author,
                    $"{channel.Name}:{message.Body}",
                    message.SentAt),
                cancellationToken);
        }

        return message;
    }

    /// <summary>One newest-window page of a channel's messages, oldest first
    /// within the page.</summary>
    public Task<NewestWindowPage<ChatMessage>> ListMessagesPageAsync(
        ChatChannelId channelId,
        int limit,
        DateTimeOffset? before,
        Guid? beforeId,
        CancellationToken cancellationToken = default)
        => _chat.ListPageByChannelAsync(channelId, limit, before, beforeId, cancellationToken);
}
