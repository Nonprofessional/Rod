using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.Persistence.Configurations;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IChatStore"/>: channels and messages
/// survive a teamserver restart. Messages are append-only; channels are
/// saved once, so there is no lost-update window to close.
/// </summary>
internal sealed class PostgresChatStore : IChatStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresChatStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task SaveChannelAsync(ChatChannel channel, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.ChatChannels.FindAsync(new object[] { channel.Id.Value }, cancellationToken);
        if (stored is null)
            db.ChatChannels.Add(StoredChatChannel.From(channel));
        else
            db.Entry(stored).CurrentValues.SetValues(StoredChatChannel.From(channel));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ChatChannel?> FindChannelAsync(ChatChannelId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.ChatChannels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id.Value, cancellationToken);
        return stored?.ToDomain();
    }

    public async Task<IReadOnlyList<ChatChannel>> ListChannelsAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ChatChannels.AsNoTracking()
            .Where(c => c.EngagementId == engagementId)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(c => c.ToDomain()).ToArray();
    }

    public async Task SaveMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        db.ChatMessages.Add(StoredChatMessage.From(message));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<NewestWindowPage<ChatMessage>> ListPageByChannelAsync(
        ChatChannelId channelId,
        int limit,
        DateTimeOffset? before,
        Guid? beforeId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        IQueryable<StoredChatMessage> query = db.ChatMessages
            .AsNoTracking()
            .Where(m => m.ChannelId == channelId.Value);
        if (before is { } afterAt && beforeId is { } afterId)
        {
            query = query.Where(m => m.SentAt < afterAt
                || (m.SentAt == afterAt && m.Id.CompareTo(afterId) < 0));
        }

        return await TakePageAsync(query, limit, cancellationToken);
    }

    public async Task<NewestWindowPage<ChatMessage>> ListPageByEngagementAsync(
        EngagementId engagementId,
        int limit,
        DateTimeOffset? before,
        Guid? beforeId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        IQueryable<StoredChatMessage> query = db.ChatMessages
            .AsNoTracking()
            .Where(m => m.EngagementId == engagementId);
        if (before is { } afterAt && beforeId is { } afterId)
        {
            query = query.Where(m => m.SentAt < afterAt
                || (m.SentAt == afterAt && m.Id.CompareTo(afterId) < 0));
        }

        return await TakePageAsync(query, limit, cancellationToken);
    }

    private static async Task<NewestWindowPage<ChatMessage>> TakePageAsync(
        IQueryable<StoredChatMessage> scoped,
        int limit,
        CancellationToken cancellationToken)
    {
        var newestFirst = await scoped
            .OrderByDescending(m => m.SentAt)
            .ThenByDescending(m => m.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = newestFirst.Count > limit;
        var items = newestFirst.Take(limit)
            .Select(m => m.ToDomain())
            .ToArray();
        Array.Reverse(items); // Oldest first within the page.
        return new NewestWindowPage<ChatMessage>(items, hasMore);
    }
}
