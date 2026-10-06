using Rod.CoreState.Live;

namespace Rod.CoreState.Automation;

/// <summary>
/// What makes a rule fire (architecture.md Sec 10.4). Two shapes, deliberately
/// few: an <see cref="Interval"/> time trigger ("every 30 minutes", durable --
/// the next-fire stamp persists with the rule) and an <see cref="Event"/>
/// trigger naming a live event kind from the engine's short whitelist
/// (best-effort like the bus it rides). Anything richer -- a script evaluator
/// over the same firing path -- is a follow-on, not a third shape here.
/// </summary>
public abstract record AutomationTrigger
{
    private AutomationTrigger()
    {
    }

    /// <summary>Fire on a fixed cadence; the schedule is durable.</summary>
    public sealed record Interval(TimeSpan Every) : AutomationTrigger;

    /// <summary>Fire when a matching live event lands on the engagement's bus.</summary>
    public sealed record Event(LiveEventKind EventKind) : AutomationTrigger;
}
