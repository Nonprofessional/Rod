using System.Collections.Concurrent;

namespace Rod.CoreState.Collaboration;

/// <summary>
/// The in-memory <see cref="IChatStore"/> twin: channels and messages live
/// as long as the process. The console reads through the same port either
/// way, so the Postgres swap changes durability, never behavior.
/// </summary>
public sealed class InMemoryChatStore : IChatStore
{
    private readonly ConcurrentDictionary<ChatChannelId, ChatChannel> _channels = new();
    private readonly ConcurrentDictionary<ChatMessageId, ChatMessage> _messages = new();

    public Task SaveChannelAsync(ChatChannel channel, CancellationToken cancellationToken = default)
    {
        _channels[channel.Id] = channel;
        return Task.CompletedTask;
    }

    public Task<ChatChannel?> FindChannelAsync(ChatChannelId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_channels.TryGetValue(id, out var channel) ? channel : null);

    public Task<IReadOnlyList<ChatChannel>> ListChannelsAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ChatChannel>>(
            _channels.Values
                .Where(c => c.EngagementId == engagementId)
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id.Value)
                .ToArray());

    public Task SaveMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        _messages[message.Id] = message;
        return Task.CompletedTask;
    }

    public Task<NewestWindowPage<ChatMessage>> ListPageByChannelAsync(
        ChatChannelId channelId,
        int limit,
        DateTimeOffset? before,
        Guid? beforeId,
        CancellationToken cancellationToken = default)
    {
        var ordered = _messages.Values
            .Where(m => m.ChannelId == channelId)
            .OrderBy(m => m.SentAt)
            .ThenBy(m => m.Id.Value)
            .ToArray();
        return Task.FromResult(CollaborationWindow.TakeNewest(
            ordered, limit, before, beforeId, m => m.SentAt, m => m.Id.Value));
    }

    public Task<NewestWindowPage<ChatMessage>> ListPageByEngagementAsync(
        EngagementId engagementId,
        int limit,
        DateTimeOffset? before,
        Guid? beforeId,
        CancellationToken cancellationToken = default)
    {
        var ordered = _messages.Values
            .Where(m => m.EngagementId == engagementId)
            .OrderBy(m => m.SentAt)
            .ThenBy(m => m.Id.Value)
            .ToArray();
        return Task.FromResult(CollaborationWindow.TakeNewest(
            ordered, limit, before, beforeId, m => m.SentAt, m => m.Id.Value));
    }
}
