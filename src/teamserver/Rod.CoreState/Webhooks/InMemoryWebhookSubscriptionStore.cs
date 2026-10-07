using System.Collections.Concurrent;

namespace Rod.CoreState.Webhooks;

/// <summary>
/// The in-memory <see cref="IWebhookSubscriptionStore"/> twin:
/// subscriptions live as long as the process. The forwarder reads through
/// the same port either way, so the Postgres swap changes durability,
/// never behavior.
/// </summary>
public sealed class InMemoryWebhookSubscriptionStore : IWebhookSubscriptionStore
{
    private readonly ConcurrentDictionary<WebhookSubscriptionId, WebhookSubscription> _subscriptions = new();

    public Task SaveAsync(WebhookSubscription subscription, CancellationToken cancellationToken = default)
    {
        _subscriptions[subscription.Id] = subscription;
        return Task.CompletedTask;
    }

    public Task<WebhookSubscription?> FindAsync(WebhookSubscriptionId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_subscriptions.TryGetValue(id, out var subscription) ? subscription : null);

    public Task<IReadOnlyList<WebhookSubscription>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WebhookSubscription>>(
            _subscriptions.Values
                .Where(s => s.EngagementId == engagementId)
                .OrderBy(s => s.CreatedAt)
                .ThenBy(s => s.Id.Value)
                .ToArray());

    public Task<IReadOnlyList<WebhookSubscription>> ListEnabledAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WebhookSubscription>>(
            _subscriptions.Values
                .Where(s => s.Enabled)
                .OrderBy(s => s.CreatedAt)
                .ThenBy(s => s.Id.Value)
                .ToArray());

    public Task<bool> RemoveAsync(WebhookSubscriptionId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_subscriptions.TryRemove(id, out _));
}
