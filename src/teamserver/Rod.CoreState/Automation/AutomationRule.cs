using Rod.CoreState.Operators;

namespace Rod.CoreState.Automation;

/// <summary>
/// One engagement-scoped automation rule (architecture.md Sec 10.4): a
/// trigger, an optional condition narrowing it, and a single action -- issue
/// one task. The rule is declarative state persisted with the engagement;
/// the engine interprets it, and every firing rides
/// <c>TaskService.IssueAsync</c> so the issuance gates hold unchanged.
///
/// The rule also carries its own bookkeeping: the durable next-fire stamp a
/// time trigger survives a restart through, the fire count and last-fire
/// stamp the cooldown and cap read, and the consecutive-refusal count that
/// ends in the engine parking a rule that keeps knocking on a closed door.
/// Mutations are the entity's own methods so the invariants (a disabled rule
/// has no next fire, a fired rule advances its stamp) live here, not in
/// whichever caller happened to mutate last.
/// </summary>
public sealed class AutomationRule
{
    public AutomationRuleId Id { get; }
    public EngagementId EngagementId { get; }

    /// <summary>The operator-facing name ("overnight-screenshots").</summary>
    public string Name { get; }

    /// <summary>What makes the rule fire.</summary>
    public AutomationTrigger Trigger { get; }

    /// <summary>
    /// The event condition: fire only for this implant's events. Null fires
    /// for any implant's. Meaningful to event triggers only; an interval
    /// trigger leaves it null.
    /// </summary>
    public ImplantId? OnlyImplant { get; }

    /// <summary>
    /// The completion condition: fire only when this verb's task completed.
    /// Null fires for any completion. Meaningful to
    /// <see cref="LiveEventKind.TaskCompleted"/> triggers only.
    /// </summary>
    public string? CompletedVerb { get; }

    /// <summary>The action: the implant every firing tasks.</summary>
    public ImplantId TargetImplant { get; }

    /// <summary>The action's verb, blocked-verb-checked at creation.</summary>
    public string Verb { get; }

    /// <summary>The action's arguments, issued verbatim on every firing.</summary>
    public string Arguments { get; }

    /// <summary>The minimum spacing between firings of this rule.</summary>
    public TimeSpan Cooldown { get; }

    /// <summary>The rule disables itself after this many firings.</summary>
    public int MaxFirings { get; }

    public bool Enabled { get; private set; }

    /// <summary>
    /// The durable schedule position of a time trigger: when the next firing
    /// is due. Advanced on every firing and on enable; null for event
    /// triggers and disabled rules. A restart reads it back and resumes --
    /// fires missed while the server was down are skipped, not made up.
    /// </summary>
    public DateTimeOffset? NextFireAt { get; private set; }

    public int FireCount { get; private set; }
    public DateTimeOffset? LastFiredAt { get; private set; }

    /// <summary>
    /// Issuance refusals in a row, reset by any successful firing. The engine
    /// disables the rule when this reaches
    /// <see cref="AutomationLimits.ConsecutiveRefusalLimit"/>.
    /// </summary>
    public int ConsecutiveRefusals { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>The operator who created the rule.</summary>
    public OperatorId CreatedBy { get; }

    /// <summary>Set when the rule was disabled (by an operator or by a guard); null while enabled.</summary>
    public DateTimeOffset? DisabledAt { get; private set; }

    public AutomationRule(
        AutomationRuleId id,
        EngagementId engagementId,
        string name,
        AutomationTrigger trigger,
        ImplantId? onlyImplant,
        string? completedVerb,
        ImplantId targetImplant,
        string verb,
        string arguments,
        TimeSpan cooldown,
        int maxFirings,
        bool enabled,
        DateTimeOffset? nextFireAt,
        int fireCount,
        DateTimeOffset? lastFiredAt,
        int consecutiveRefusals,
        DateTimeOffset createdAt,
        OperatorId createdBy,
        DateTimeOffset? disabledAt)
    {
        Id = id;
        EngagementId = engagementId;
        Name = name;
        Trigger = trigger;
        OnlyImplant = onlyImplant;
        CompletedVerb = completedVerb;
        TargetImplant = targetImplant;
        Verb = verb;
        Arguments = arguments;
        Cooldown = cooldown;
        MaxFirings = maxFirings;
        Enabled = enabled;
        NextFireAt = nextFireAt;
        FireCount = fireCount;
        LastFiredAt = lastFiredAt;
        ConsecutiveRefusals = consecutiveRefusals;
        CreatedAt = createdAt;
        CreatedBy = createdBy;
        DisabledAt = disabledAt;
    }

    /// <summary>
    /// Creates a rule with its schedule armed. The first firing of a time
    /// trigger is one interval out; an event trigger waits for its event.
    /// Throws <see cref="ArgumentException"/> on out-of-bounds shape (the
    /// boundary values live on <see cref="AutomationLimits"/>).
    /// </summary>
    public static AutomationRule Create(
        AutomationRuleId id,
        EngagementId engagementId,
        string name,
        AutomationTrigger trigger,
        ImplantId? onlyImplant,
        string? completedVerb,
        ImplantId targetImplant,
        string verb,
        string arguments,
        TimeSpan cooldown,
        int maxFirings,
        DateTimeOffset createdAt,
        OperatorId createdBy)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A rule needs a name.", nameof(name));
        if (cooldown < AutomationLimits.MinCooldown)
            throw new ArgumentException($"Cooldown must be at least {AutomationLimits.MinCooldown}.");
        if (maxFirings < 1 || maxFirings > AutomationLimits.MaxFiringsCeiling)
            throw new ArgumentException($"Firing cap must be between 1 and {AutomationLimits.MaxFiringsCeiling}.");
        if (string.IsNullOrWhiteSpace(verb))
            throw new ArgumentException("A rule needs a verb.", nameof(verb));
        if (arguments.Length > AutomationLimits.MaxArgumentBytes)
            throw new ArgumentException($"Arguments exceed {AutomationLimits.MaxArgumentBytes} bytes.");
        if (trigger is AutomationTrigger.Interval interval
            && (interval.Every < AutomationLimits.MinInterval || interval.Every > AutomationLimits.MaxInterval))
        {
            throw new ArgumentException($"Interval must be between {AutomationLimits.MinInterval} and {AutomationLimits.MaxInterval}.");
        }

        var nextFireAt = trigger is AutomationTrigger.Interval cadence
            ? createdAt + cadence.Every
            : (DateTimeOffset?)null;
        return new AutomationRule(
            id,
            engagementId,
            name.Trim(),
            trigger,
            onlyImplant,
            completedVerb,
            targetImplant,
            verb,
            arguments,
            cooldown,
            maxFirings,
            enabled: true,
            nextFireAt,
            fireCount: 0,
            lastFiredAt: null,
            consecutiveRefusals: 0,
            createdAt,
            createdBy,
            disabledAt: null);
    }

    /// <summary>Whether the time trigger is due right now. Event rules are never "due" -- the bus decides.</summary>
    public bool IsDue(DateTimeOffset now)
        => Enabled && Trigger is AutomationTrigger.Interval && NextFireAt is { } due && due <= now;

    /// <summary>Whether the cooldown spacing allows a firing right now.</summary>
    public bool CooldownPassed(DateTimeOffset now)
        => LastFiredAt is null || now - LastFiredAt >= Cooldown;

    /// <summary>Whether the rule has spent its firing cap.</summary>
    public bool CapReached => FireCount >= MaxFirings;

    /// <summary>
    /// Records one successful firing: advances the stamp, resets the refusal
    /// streak. The engine persists the returned state on the same breath as
    /// the issuance it records.
    /// </summary>
    public void RecordFire(DateTimeOffset at)
    {
        FireCount++;
        LastFiredAt = at;
        ConsecutiveRefusals = 0;
        if (Trigger is AutomationTrigger.Interval interval)
            NextFireAt = at + interval.Every;
    }

    /// <summary>
    /// Records one issuance refusal and schedules the retry (if the trigger
    /// has a cadence) for the next period -- a refused rule waits out its
    /// interval rather than retrying in a tight loop.
    /// </summary>
    public void RecordRefusal(DateTimeOffset at)
    {
        ConsecutiveRefusals++;
        if (Trigger is AutomationTrigger.Interval interval)
            NextFireAt = at + interval.Every;
    }

    /// <summary>
    /// Disables the rule (the cancel). Idempotent: a second call changes
    /// nothing and returns false.
    /// </summary>
    public bool Disable(DateTimeOffset at)
    {
        if (!Enabled)
            return false;

        Enabled = false;
        DisabledAt = at;
        NextFireAt = null;
        return true;
    }

    /// <summary>
    /// Re-arms the rule from now: one fresh interval (or the next event)
    /// before the first firing, the refusal streak carried over -- a rule the
    /// engine parked for refusals does not resume into the same wall.
    /// </summary>
    public bool Enable(DateTimeOffset at)
    {
        if (Enabled)
            return false;

        Enabled = true;
        DisabledAt = null;
        if (Trigger is AutomationTrigger.Interval interval)
            NextFireAt = at + interval.Every;
        return true;
    }
}
