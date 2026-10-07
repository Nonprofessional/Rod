namespace Rod.CoreState.Webhooks;

/// <summary>
/// The durable home for engagement-scoped webhook subscriptions
/// (architecture.md Sec 4.4). Subscriptions persist with the engagement --
/// the Postgres-backed adapter swaps in when
/// <c>ConnectionStrings:Postgres</c> is set, bookkeeping included, so a
/// registered channel survives a teamserver restart. In-memory by default,
/// the same posture as the rest of core state.
/// </summary>
public interface IWebhookSubscriptionStore
{
    /// <summary>Upserts a subscription (the id is the identity; a delivery re-saves its bookkeeping).</summary>
    Task SaveAsync(WebhookSubscription subscription, CancellationToken cancellationToken = default);

    /// <summary>The subscription, or null when unknown.</summary>
    Task<WebhookSubscription?> FindAsync(WebhookSubscriptionId id, CancellationToken cancellationToken = default);

    /// <summary>The subscriptions belonging to one engagement, oldest first.</summary>
    Task<IReadOnlyList<WebhookSubscription>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every enabled subscription across engagements, oldest first. The
    /// forwarder's subscription reconcile starts here; subscriptions are
    /// few and the scan runs every few seconds, so one process-wide read is
    /// the whole cost.
    /// </summary>
    Task<IReadOnlyList<WebhookSubscription>> ListEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a subscription. Returns false when it was not stored.</summary>
    Task<bool> RemoveAsync(WebhookSubscriptionId id, CancellationToken cancellationToken = default);
}
