using Rod.CoreState.Live;
using Rod.CoreState.Operators;

namespace Rod.CoreState.Webhooks;

/// <summary>
/// One engagement-scoped webhook subscription (architecture.md Sec 4.4): a
/// display name, a target URL, and the event kinds to push there. The
/// subscription is declarative state persisted with the engagement; the
/// forwarder interprets it, and the URL is the channel's bearer secret --
/// it is never written to the audit trail.
///
/// The subscription also carries its own bookkeeping: the delivery count
/// and last-delivery stamp the operator surface reads, and the
/// consecutive-failure count that ends in the forwarder parking a
/// subscription whose receiver is gone. Mutations are the entity's own
/// methods so the invariants (a parked subscription delivers nothing, a
/// delivery resets the failure run) live here, not in whichever caller
/// happened to mutate last.
/// </summary>
public sealed class WebhookSubscription
{
    public WebhookSubscriptionId Id { get; }
    public EngagementId EngagementId { get; }

    /// <summary>The operator-facing name ("overnight-watch").</summary>
    public string Name { get; }

    /// <summary>
    /// The push target, absolute https (loopback http aside). The query may
    /// carry the receiver's bearer secret; treat the whole string as one.
    /// </summary>
    public string Url { get; }

    /// <summary>The event kinds to forward, deduplicated, in the order given.</summary>
    public IReadOnlyList<LiveEventKind> EventKinds { get; }

    public bool Enabled { get; private set; }

    public int DeliveryCount { get; private set; }

    public DateTimeOffset? LastDeliveredAt { get; private set; }

    /// <summary>
    /// Delivery failures in a row, reset by any success. The forwarder
    /// parks the subscription when this reaches
    /// <see cref="WebhookLimits.ConsecutiveFailureLimit"/>.
    /// </summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>The readable reason for the most recent failure; the surface shows it.</summary>
    public string? LastFailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>The operator who registered the subscription.</summary>
    public OperatorId CreatedBy { get; }

    /// <summary>Set when disabled (by an operator or by the failure guard); null while enabled.</summary>
    public DateTimeOffset? DisabledAt { get; private set; }

    public WebhookSubscription(
        WebhookSubscriptionId id,
        EngagementId engagementId,
        string name,
        string url,
        IReadOnlyList<LiveEventKind> eventKinds,
        bool enabled,
        int deliveryCount,
        DateTimeOffset? lastDeliveredAt,
        int consecutiveFailures,
        string? lastFailureReason,
        DateTimeOffset createdAt,
        OperatorId createdBy,
        DateTimeOffset? disabledAt)
    {
        Id = id;
        EngagementId = engagementId;
        Name = name;
        Url = url;
        EventKinds = eventKinds;
        Enabled = enabled;
        DeliveryCount = deliveryCount;
        LastDeliveredAt = lastDeliveredAt;
        ConsecutiveFailures = consecutiveFailures;
        LastFailureReason = lastFailureReason;
        CreatedAt = createdAt;
        CreatedBy = createdBy;
        DisabledAt = disabledAt;
    }

    /// <summary>
    /// Creates a subscription, enabled. Throws
    /// <see cref="ArgumentException"/> on out-of-bounds shape (the boundary
    /// values live on <see cref="WebhookLimits"/>).
    /// </summary>
    public static WebhookSubscription Create(
        WebhookSubscriptionId id,
        EngagementId engagementId,
        string name,
        string url,
        IEnumerable<LiveEventKind> eventKinds,
        DateTimeOffset createdAt,
        OperatorId createdBy)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A webhook subscription needs a name.", nameof(name));
        if (name.Trim().Length > WebhookLimits.MaxNameBytes)
            throw new ArgumentException($"The name exceeds {WebhookLimits.MaxNameBytes} bytes.");
        var urlError = WebhookLimits.ValidateUrl(url);
        if (urlError is not null)
            throw new ArgumentException(urlError, nameof(url));
        var kinds = eventKinds.Distinct().ToArray();
        if (kinds.Length == 0)
            throw new ArgumentException("A webhook subscription needs at least one event kind.", nameof(eventKinds));
        foreach (var kind in kinds)
        {
            if (!WebhookLimits.IsNotifiableKind(kind))
                throw new ArgumentException($"{kind} is not notifiable.", nameof(eventKinds));
        }

        return new WebhookSubscription(
            id,
            engagementId,
            name.Trim(),
            url.Trim(),
            kinds,
            enabled: true,
            deliveryCount: 0,
            lastDeliveredAt: null,
            consecutiveFailures: 0,
            lastFailureReason: null,
            createdAt,
            createdBy,
            disabledAt: null);
    }

    /// <summary>Whether a live event of this kind belongs in a push to this subscription.</summary>
    public bool IsNotifiedBy(LiveEventKind kind)
        => Enabled && EventKinds.Contains(kind);

    /// <summary>
    /// Records one successful delivery: advances the count and resets the
    /// failure run. The forwarder persists the state on the same breath as
    /// the audit fact it records.
    /// </summary>
    public void RecordDelivery(DateTimeOffset at)
    {
        DeliveryCount++;
        LastDeliveredAt = at;
        ConsecutiveFailures = 0;
        LastFailureReason = null;
    }

    /// <summary>
    /// Records one failed delivery attempt. Returns true when this failure
    /// reached the consecutive limit and parked the subscription.
    /// </summary>
    public bool RecordFailure(DateTimeOffset at, string reason)
    {
        ConsecutiveFailures++;
        LastFailureReason = reason;
        if (ConsecutiveFailures < WebhookLimits.ConsecutiveFailureLimit)
            return false;

        Disable(at);
        return true;
    }

    /// <summary>
    /// Disables the subscription (the cancel). Idempotent: a second call
    /// changes nothing and returns false.
    /// </summary>
    public bool Disable(DateTimeOffset at)
    {
        if (!Enabled)
            return false;

        Enabled = false;
        DisabledAt = at;
        return true;
    }

    /// <summary>
    /// Re-enables the subscription with the failure run cleared --
    /// re-enabling is the operator saying the receiver is fixed, so the
    /// watch starts fresh rather than one failure from parking again.
    /// </summary>
    public bool Enable(DateTimeOffset at)
    {
        if (Enabled)
            return false;

        Enabled = true;
        DisabledAt = null;
        ConsecutiveFailures = 0;
        LastFailureReason = null;
        return true;
    }
}
