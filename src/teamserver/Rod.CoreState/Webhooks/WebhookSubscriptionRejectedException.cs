namespace Rod.CoreState.Webhooks;

/// <summary>
/// A webhook-subscription operation was refused -- the engagement is
/// closed, the per-engagement cap is spent, or the shape failed a guard
/// the surface wanted named as a domain refusal (architecture.md Sec 4.4).
/// A client-correctable domain refusal: the caller maps it to a wire
/// status the way <see cref="Automation.AutomationRuleRejectedException"/>
/// is mapped.
/// </summary>
public sealed class WebhookSubscriptionRejectedException : DomainException
{
    public WebhookSubscriptionRejectedException(string message)
        : base(message)
    {
    }
}
