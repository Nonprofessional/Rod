namespace Rod.CoreState.Automation;

/// <summary>
/// An automation-rule operation was refused -- the verb is blocked for
/// unattended firing, the event kind is not triggerable, the target implant
/// is foreign or retired, or the engagement is closed (architecture.md
/// Sec 10.4). A client-correctable domain refusal: the caller maps it to a
/// wire status the way <see cref="TaskRejectedException"/> is mapped.
/// </summary>
public sealed class AutomationRuleRejectedException : DomainException
{
    public AutomationRuleRejectedException(string message)
        : base(message)
    {
    }
}
