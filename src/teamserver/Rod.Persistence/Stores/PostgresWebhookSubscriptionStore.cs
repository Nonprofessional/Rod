using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Webhooks;
using Rod.Persistence.Configurations;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IWebhookSubscriptionStore"/> (architecture.md
/// Sec 4.4): engagement-scoped webhook subscriptions survive a teamserver
/// restart, delivery bookkeeping riding the row. Plain rows, no concurrency
/// hazard: deliveries serialize in the forwarder's pump and mutations in
/// the subscription service, and a second teamserver against the same
/// database is not a supported shape.
/// </summary>
internal sealed class PostgresWebhookSubscriptionStore : IWebhookSubscriptionStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresWebhookSubscriptionStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task SaveAsync(WebhookSubscription subscription, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.WebhookSubscriptions.FindAsync(new object[] { subscription.Id.Value }, cancellationToken);
        if (stored is null)
        {
            db.WebhookSubscriptions.Add(StoredWebhookSubscription.From(subscription));
        }
        else
        {
            // The row mirrors the aggregate whole; the id and the engagement
            // binding never change, everything else may have.
            db.Entry(stored).CurrentValues.SetValues(StoredWebhookSubscription.From(subscription));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WebhookSubscription?> FindAsync(WebhookSubscriptionId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.WebhookSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id.Value, cancellationToken);
        return stored?.ToDomain();
    }

    public async Task<IReadOnlyList<WebhookSubscription>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var mine = await db.WebhookSubscriptions.AsNoTracking()
            .Where(s => s.EngagementId == engagementId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
        return mine.Select(s => s.ToDomain()).ToArray();
    }

    public async Task<IReadOnlyList<WebhookSubscription>> ListEnabledAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var enabled = await db.WebhookSubscriptions.AsNoTracking()
            .Where(s => s.Enabled)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
        return enabled.Select(s => s.ToDomain()).ToArray();
    }

    public async Task<bool> RemoveAsync(WebhookSubscriptionId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.WebhookSubscriptions.FindAsync(new object[] { id.Value }, cancellationToken);
        if (stored is null)
            return false;
        db.WebhookSubscriptions.Remove(stored);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
