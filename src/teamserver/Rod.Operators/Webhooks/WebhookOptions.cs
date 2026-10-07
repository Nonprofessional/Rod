namespace Rod.Operators.Webhooks;

/// <summary>
/// Forwarder timing, bound from the <c>Webhooks</c> section. Two knobs on
/// purpose: the tick only decides how soon the forwarder notices a new or
/// changed subscription, and the delivery timeout bounds one push so a
/// stalled receiver cannot hold its engagement's pump.
/// </summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>
    /// How often the forwarder reconciles its per-engagement subscriptions
    /// from the store. A registration is live within one tick -- the same
    /// eventual-read posture the automation engine holds.
    /// </summary>
    public int EngineTickSeconds { get; set; } = 5;

    /// <summary>
    /// The per-request budget for one push. Delivery is single-attempt: a
    /// receiver that cannot answer inside this window is a failure the
    /// trail records, not a backlog the engine carries.
    /// </summary>
    public int DeliveryTimeoutSeconds { get; set; } = 10;
}
