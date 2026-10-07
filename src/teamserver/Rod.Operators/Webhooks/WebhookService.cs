using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Webhooks;

namespace Rod.Operators.Webhooks;

/// <summary>
/// The subscription-management use cases (architecture.md Sec 4.4):
/// register, list, read, enable, disable, delete, and the
/// <c>:test</c> push that verifies a channel before an operator relies on
/// it. Registration validates eagerly -- the engagement open, the kinds
/// notifiable, the URL inside the posture, the per-engagement cap -- so an
/// operator's mistake is a refusal at registration instead of a quiet
/// miss three hours later. Every mutation lands in the engagement trail
/// attributed to the mutating operator.
/// </summary>
public sealed class WebhookService
{
    private readonly IWebhookSubscriptionStore _subscriptions;
    private readonly IEngagementRepository _engagements;
    private readonly IAuditStore _audit;
    private readonly WebhookPusher _pusher;
    private readonly TimeProvider _clock;

    public WebhookService(
        IWebhookSubscriptionStore subscriptions,
        IEngagementRepository engagements,
        IAuditStore audit,
        WebhookPusher pusher,
        TimeProvider clock)
    {
        _subscriptions = subscriptions;
        _engagements = engagements;
        _audit = audit;
        _pusher = pusher;
        _clock = clock;
    }

    /// <summary>What the operator asked to persist; validation and the id resolve here.</summary>
    public sealed record RegisterCommand(
        EngagementId EngagementId,
        string Name,
        string Url,
        IReadOnlyList<LiveEventKind> EventKinds,
        OperatorId RegisteredBy);

    public async Task<WebhookSubscription> RegisterAsync(RegisterCommand command, CancellationToken cancellationToken = default)
    {
        var engagement = await _engagements.FindAsync(command.EngagementId, cancellationToken)
            ?? throw new WebhookSubscriptionRejectedException("Unknown engagement.");
        if (engagement.IsClosed || engagement.IsRetired)
            throw new WebhookSubscriptionRejectedException("The engagement is closed; it accepts no new channels.");

        var existing = await _subscriptions.ListByEngagementAsync(command.EngagementId, cancellationToken);
        if (existing.Count >= WebhookLimits.MaxSubscriptionsPerEngagement)
            throw new WebhookSubscriptionRejectedException(
                $"The engagement already holds {existing.Count} webhook subscriptions (limit {WebhookLimits.MaxSubscriptionsPerEngagement}).");

        if (command.EventKinds.Count == 0)
            throw new WebhookSubscriptionRejectedException("A webhook subscription needs at least one event kind.");
        foreach (var kind in command.EventKinds)
        {
            if (!WebhookLimits.IsNotifiableKind(kind))
                throw new WebhookSubscriptionRejectedException($"The {kind} event is not notifiable.");
        }
        var urlError = WebhookLimits.ValidateUrl(command.Url);
        if (urlError is not null)
            throw new WebhookSubscriptionRejectedException(urlError);

        var subscription = WebhookSubscription.Create(
            WebhookSubscriptionId.New(),
            command.EngagementId,
            command.Name,
            command.Url,
            command.EventKinds,
            _clock.GetUtcNow(),
            command.RegisteredBy);

        await _subscriptions.SaveAsync(subscription, cancellationToken);
        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: subscription.EngagementId.Value,
                operatorId: subscription.CreatedBy.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "webhook.registered",
                kind: AuditEventKind.WebhookSubscriptionCreated,
                payload: $"registered '{subscription.Name}' on {DescribeKinds(subscription)}",
                output: null,
                outcome: subscription.Id.ToString(),
                at: subscription.CreatedAt),
            cancellationToken);
        return subscription;
    }

    public async Task<IReadOnlyList<WebhookSubscription>> ListAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
        => await _subscriptions.ListByEngagementAsync(engagementId, cancellationToken);

    public async Task<WebhookSubscription?> FindAsync(
        EngagementId engagementId,
        WebhookSubscriptionId id,
        CancellationToken cancellationToken = default)
    {
        var subscription = await _subscriptions.FindAsync(id, cancellationToken);
        return subscription is { EngagementId: var scope } && scope == engagementId ? subscription : null;
    }

    /// <summary>
    /// Re-enables the subscription with the failure run cleared. Returns
    /// the stored subscription; the state change is idempotent.
    /// </summary>
    public async Task<WebhookSubscription> EnableAsync(
        EngagementId engagementId,
        WebhookSubscriptionId id,
        OperatorId changedBy,
        CancellationToken cancellationToken = default)
        => await TransitionAsync(engagementId, id, changedBy, enable: true, cancellationToken);

    /// <summary>The cancel: a disabled subscription pushes nothing until re-enabled. Idempotent.</summary>
    public async Task<WebhookSubscription> DisableAsync(
        EngagementId engagementId,
        WebhookSubscriptionId id,
        OperatorId changedBy,
        CancellationToken cancellationToken = default)
        => await TransitionAsync(engagementId, id, changedBy, enable: false, cancellationToken);

    /// <summary>
    /// Pushes one test frame through the ordinary delivery path, whatever
    /// the subscription's state -- testing a parked channel is how the
    /// operator decides to re-enable it. The attempt lands in the trail
    /// with a <c>tested:</c> outcome.
    /// </summary>
    public async Task<WebhookPusher.Result> TestDeliverAsync(
        EngagementId engagementId,
        WebhookSubscriptionId id,
        OperatorId testedBy,
        CancellationToken cancellationToken = default)
    {
        var subscription = await FindAsync(engagementId, id, cancellationToken)
            ?? throw new InvalidOperationException("The webhook subscription does not exist in this engagement.");

        var at = _clock.GetUtcNow();
        var result = await _pusher.PushAsync(
            subscription.Url,
            WebhookPusher.TestFrame(subscription.EngagementId, subscription.Name, at),
            cancellationToken);
        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: subscription.EngagementId.Value,
                operatorId: testedBy.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "webhook.delivered",
                kind: AuditEventKind.WebhookDelivered,
                payload: $"'{subscription.Name}' ({subscription.Id}) tested by operator",
                output: null,
                outcome: $"tested:{result.Outcome}",
                at: at),
            cancellationToken);
        return result;
    }

    /// <summary>Deletes the subscription outright. Returns false when it was not stored in this engagement.</summary>
    public async Task<bool> DeleteAsync(
        EngagementId engagementId,
        WebhookSubscriptionId id,
        OperatorId deletedBy,
        CancellationToken cancellationToken = default)
    {
        var subscription = await FindAsync(engagementId, id, cancellationToken);
        if (subscription is null)
            return false;

        await _subscriptions.RemoveAsync(id, cancellationToken);
        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: subscription.EngagementId.Value,
                operatorId: deletedBy.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "webhook.deleted",
                kind: AuditEventKind.WebhookSubscriptionDeleted,
                payload: $"deleted '{subscription.Name}' on {DescribeKinds(subscription)}",
                output: null,
                outcome: subscription.Id.ToString(),
                at: _clock.GetUtcNow()),
            cancellationToken);
        return true;
    }

    private async Task<WebhookSubscription> TransitionAsync(
        EngagementId engagementId,
        WebhookSubscriptionId id,
        OperatorId changedBy,
        bool enable,
        CancellationToken cancellationToken)
    {
        var subscription = await FindAsync(engagementId, id, cancellationToken)
            ?? throw new InvalidOperationException("The webhook subscription does not exist in this engagement.");

        var now = _clock.GetUtcNow();
        var changed = enable ? subscription.Enable(now) : subscription.Disable(now);
        if (!changed)
            return subscription;

        await _subscriptions.SaveAsync(subscription, cancellationToken);
        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: subscription.EngagementId.Value,
                operatorId: changedBy.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "webhook.updated",
                kind: AuditEventKind.WebhookSubscriptionUpdated,
                payload: $"{(enable ? "operator-enabled" : "operator-disabled")} '{subscription.Name}' ({subscription.Id})",
                output: null,
                outcome: subscription.Id.ToString(),
                at: now),
            cancellationToken);
        return subscription;
    }

    internal static string DescribeKinds(WebhookSubscription subscription)
        => string.Join(", ", subscription.EventKinds);
}
