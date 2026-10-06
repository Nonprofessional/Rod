namespace Rod.Operators.Automation;

/// <summary>
/// Engine timing, bound from the <c>Automation</c> section. One knob on
/// purpose: the tick is a scanning cadence, not a firing cadence -- rules
/// carry their own intervals, cooldowns, and caps, and the tick only decides
/// how soon the engine notices a due rule or a changed rule set.
/// </summary>
public sealed class AutomationOptions
{
    public const string SectionName = "Automation";

    /// <summary>
    /// How often the engine scans for due time rules and reconciles event
    /// subscriptions. An operator's rule mutation is seen within one tick --
    /// the same eventual-read posture the session staleness sweeper holds.
    /// </summary>
    public int EngineTickSeconds { get; set; } = 5;
}
