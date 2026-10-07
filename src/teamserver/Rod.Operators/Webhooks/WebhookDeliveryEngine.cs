using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Live;
using Rod.CoreState.Webhooks;
using Task = System.Threading.Tasks.Task;

namespace Rod.Operators.Webhooks;

// The domain's task entity shadows the BCL name; the engine speaks the BCL
// one throughout.

/// <summary>
/// The notification forwarder (architecture.md Sec 4.4): one hosted
/// background service that pushes selected live events to the engagement's
/// registered webhook channels while no operator watches. One subscription
/// per engagement on the live event bus, reconciled on a fixed-delay tick;
/// each matching event becomes one single-attempt POST whose body is the
/// SSE frame shape verbatim.
///
/// The bus's posture carries end to end: pushes are best-effort
/// (process-local, drop-oldest under a slow consumer, no replay), every
/// attempt lands in the trail as a <see cref="AuditEventKind.WebhookDelivered"/>
/// fact, and a receiver that fails consecutively parks its subscription
/// with the cause named. Deliveries run sequentially inside each
/// engagement's pump -- a slow receiver delays later pushes to itself,
/// never the bus and never an operator's SSE stream.
/// </summary>
public sealed class WebhookDeliveryEngine : BackgroundService
{
    private readonly IWebhookSubscriptionStore _subscriptions;
    private readonly ILiveEventBus _bus;
    private readonly WebhookPusher _pusher;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _clock;
    private readonly WebhookOptions _options;
    private readonly ILogger<WebhookDeliveryEngine> _logger;

    private readonly object _subscriptionLock = new();
    private readonly Dictionary<EngagementId, CancellationTokenSource> _pumps = new();

    public WebhookDeliveryEngine(
        IWebhookSubscriptionStore subscriptions,
        ILiveEventBus bus,
        WebhookPusher pusher,
        IAuditStore audit,
        TimeProvider clock,
        IOptions<WebhookOptions> options,
        ILogger<WebhookDeliveryEngine> logger)
    {
        _subscriptions = subscriptions;
        _bus = bus;
        _pusher = pusher;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromSeconds(Math.Max(1, _options.EngineTickSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed tick costs one scan, never the forwarder: the loop
                // rides on and the next tick re-reads the store.
                _logger.LogError(ex, "Webhook forwarder tick failed.");
            }

            try
            {
                await Task.Delay(tick, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        RetireSubscriptions();
    }

    /// <summary>
    /// One scan: reconcile the per-engagement subscriptions against the
    /// enabled set. Public because the tests drive it directly -- a tick is
    /// a pure scan of the store, so the loop around it is timing and
    /// nothing else.
    /// </summary>
    public Task TickOnceAsync(CancellationToken cancellationToken = default)
    {
        // The store read runs inside the pumps' hot path too, so the tick's
        // own read is just the reconcile's answer to "who still listens".
        return ReconcileFromStoreAsync(cancellationToken);
    }

    private async Task ReconcileFromStoreAsync(CancellationToken cancellationToken)
    {
        var enabled = await _subscriptions.ListEnabledAsync(cancellationToken);
        var engagements = enabled
            .Select(s => s.EngagementId)
            .Distinct()
            .ToArray();
        ReconcileSubscriptions(engagements);
    }

    /// <summary>
    /// One subscription per engagement holding enabled subscriptions. The
    /// reconcile runs every tick, so a registration is live within one
    /// tick; draining is sequential per engagement -- a slow receiver
    /// delays later pushes for the forwarder only, never for an operator's
    /// SSE stream (each subscriber owns its channel).
    /// </summary>
    private void ReconcileSubscriptions(IReadOnlyCollection<EngagementId> engagements)
    {
        lock (_subscriptionLock)
        {
            foreach (var retired in _pumps.Keys.Except(engagements).ToArray())
            {
                if (_pumps.Remove(retired, out var cts))
                    cts.Cancel();
            }

            foreach (var added in engagements.Except(_pumps.Keys))
            {
                var cts = new CancellationTokenSource();
                _pumps[added] = cts;
                _ = PumpAsync(added, cts);
            }
        }
    }

    private async Task PumpAsync(EngagementId engagement, CancellationTokenSource subscription)
    {
        try
        {
            await foreach (var @event in _bus.SubscribeAsync(engagement, subscription.Token))
            {
                await HandleEventAsync(engagement, @event, subscription.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The subscription was retired; nothing left to drain.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook pump for engagement {Engagement} ended; the next tick re-subscribes.", engagement);
        }
        finally
        {
            subscription.Dispose();
        }
    }

    private async Task HandleEventAsync(EngagementId engagement, LiveEvent @event, CancellationToken cancellationToken)
    {
        // A fresh read per event, the same eventual-read posture the
        // automation engine holds: a subscription disabled a moment ago is
        // honored here, not on the next tick.
        var enabled = await _subscriptions.ListEnabledAsync(cancellationToken);
        foreach (var subscription in enabled)
        {
            if (subscription.EngagementId != engagement || !subscription.IsNotifiedBy(@event.Kind))
                continue;

            await DeliverAsync(subscription, @event, cancellationToken);
        }
    }

    private async Task DeliverAsync(WebhookSubscription subscription, LiveEvent @event, CancellationToken cancellationToken)
    {
        var result = await _pusher.PushAsync(subscription.Url, WebhookPusher.Frame(@event), cancellationToken);
        var now = _clock.GetUtcNow();
        var parked = false;
        if (result.Delivered)
        {
            subscription.RecordDelivery(now);
        }
        else
        {
            parked = subscription.RecordFailure(now, result.Reason);
            _logger.LogWarning(
                "Webhook push for subscription {Subscription} failed ({Reason}); {Streak} consecutive.",
                subscription.Id, result.Reason, subscription.ConsecutiveFailures);
        }

        await _subscriptions.SaveAsync(subscription, cancellationToken);
        await AppendDeliveredAuditAsync(subscription, @event, result, now, cancellationToken);
        if (parked)
        {
            await AppendAutoDisableAuditAsync(subscription, now, cancellationToken);
        }
    }

    /// <summary>
    /// The delivery fact. The URL never enters it -- it is the channel's
    /// bearer secret; the trail names the subscription and the event.
    /// </summary>
    private Task AppendDeliveredAuditAsync(
        WebhookSubscription subscription,
        LiveEvent @event,
        WebhookPusher.Result result,
        DateTimeOffset at,
        CancellationToken cancellationToken)
        => _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: subscription.EngagementId.Value,
                operatorId: Guid.Empty,
                implantId: @event.ImplantId?.Value ?? Guid.Empty,
                taskId: @event.TaskId?.Value ?? Guid.Empty,
                verb: "webhook.delivered",
                kind: AuditEventKind.WebhookDelivered,
                payload: $"'{subscription.Name}' ({subscription.Id}) pushed {@event.Kind}"
                    + (@event.ImplantId is { } implant ? $" implant={implant}" : string.Empty)
                    + (@event.TaskId is { } task ? $" task={task}" : string.Empty),
                output: null,
                outcome: result.Outcome,
                at: at),
            cancellationToken);

    private Task AppendAutoDisableAuditAsync(
        WebhookSubscription subscription,
        DateTimeOffset at,
        CancellationToken cancellationToken)
        => _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: subscription.EngagementId.Value,
                operatorId: Guid.Empty,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "webhook.updated",
                kind: AuditEventKind.WebhookSubscriptionUpdated,
                payload: $"auto-disabled (consecutive delivery failures) '{subscription.Name}' ({subscription.Id})"
                    + $" last failure: {subscription.LastFailureReason}",
                output: null,
                outcome: subscription.Id.ToString(),
                at: at),
            cancellationToken);

    private void RetireSubscriptions()
    {
        lock (_subscriptionLock)
        {
            foreach (var cts in _pumps.Values)
                cts.Cancel();
            _pumps.Clear();
        }
    }
}
