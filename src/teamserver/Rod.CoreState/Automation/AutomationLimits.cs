using Rod.CoreState.Live;
using Rod.CoreState.Tasks;

namespace Rod.CoreState.Automation;

/// <summary>
/// The engine's standing boundaries (architecture.md Sec 10.4): what a rule
/// may ask for, how often, how many times, and which verbs it may never
/// carry. These are domain rules, not engine knobs -- every rule passes them
/// at creation, so a rule stored in the engagement is a rule the guards
/// already vouched for.
/// </summary>
public static class AutomationLimits
{
    /// <summary>
    /// The shortest cadence a time trigger may run at. Practical schedules are
    /// minutes; seconds exist so the lab and the tests can watch a rule fire.
    /// </summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);

    /// <summary>The longest cadence a time trigger may run at (a month of quiet).</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(30);

    /// <summary>The shortest cooldown between firings of one rule.</summary>
    public static readonly TimeSpan MinCooldown = TimeSpan.FromSeconds(5);

    /// <summary>What an event rule's cooldown defaults to when the operator names none.</summary>
    public static readonly TimeSpan DefaultEventCooldown = TimeSpan.FromMinutes(1);

    /// <summary>What a rule's firing cap defaults to when the operator names none.</summary>
    public const int DefaultMaxFirings = 100;

    /// <summary>The most firings a rule may be capped at.</summary>
    public const int MaxFiringsCeiling = 1000;

    /// <summary>
    /// How many rules one engagement may hold. Rules are standing tasking;
    /// an engagement that needs more than this has a scripting problem, not
    /// a rules problem.
    /// </summary>
    public const int MaxRulesPerEngagement = 50;

    /// <summary>
    /// How many consecutive issuance refusals a rule tolerates before the
    /// engine disables it -- three ROE violations in a row mean the rule is
    /// knocking on a door the engagement closed.
    /// </summary>
    public const int ConsecutiveRefusalLimit = 3;

    /// <summary>Bounds the action's arguments string, the same ceiling task issuance applies.</summary>
    public const int MaxArgumentBytes = 512 * 1024;

    /// <summary>Bounds the rule's display name.</summary>
    public const int MaxNameBytes = 200;

    /// <summary>
    /// The Windows-sensitive three (architecture.md Sec 12.2): injection,
    /// memory dumping, and input capture never fire unattended. This is the
    /// static floor of the sensitivity judgment -- what
    /// <c>DefaultSensitiveVerbPolicy</c> serves and what the registry-backed
    /// policy keeps loaded underneath its metadata-driven answer. The
    /// authoritative judgment for any registered verb is its descriptor's
    /// OPSEC metadata (the tradecraft registry); a verb nobody registered and
    /// nobody listed here is not sensitive -- the dispatch gate stops it.
    /// </summary>
    private static readonly string[] SensitiveVerbs =
    {
        "inject.shellcode",
        "collect.minidump",
        "collect.keylog",
    };

    /// <summary>
    /// Whether a rule may carry this verb: the channel verbs (an unattended
    /// firing cannot own an interactive input half) plus everything the
    /// sensitivity floor lists (<see cref="IsSensitiveVerb"/>). Matching is
    /// case-insensitive, the same rule every verb gate honors.
    /// </summary>
    public static bool IsBlockedVerb(string? verb)
        => string.IsNullOrWhiteSpace(verb)
            || ChannelVerbs.IsChannelVerb(verb)
            || IsSensitiveVerb(verb);

    /// <summary>
    /// The static sensitivity floor: the sensitive three and the
    /// evasion/exploit namespaces (contract-only categories whose tradecraft
    /// is out-of-tree). The channel verbs are deliberately absent -- they are
    /// blocked for automation by shape, not by sensitivity.
    /// </summary>
    public static bool IsSensitiveVerb(string? verb)
    {
        if (string.IsNullOrWhiteSpace(verb))
            return false;
        foreach (var sensitive in SensitiveVerbs)
        {
            if (string.Equals(sensitive, verb, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return verb.StartsWith("evasion.", StringComparison.OrdinalIgnoreCase)
            || verb.StartsWith("exploit.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The live event kinds an event trigger may name. The engagement's
    /// operational beats are triggerable; operator presence is console
    /// chatter and channel output is a per-chunk firehose, so neither is.
    /// </summary>
    public static bool IsSupportedEventTrigger(LiveEventKind kind) => kind switch
    {
        LiveEventKind.SessionOpened => true,
        LiveEventKind.SessionClosed => true,
        LiveEventKind.TaskIssued => true,
        LiveEventKind.TaskCompleted => true,
        LiveEventKind.TaskCancelled => true,
        LiveEventKind.ImplantRetired => true,
        _ => false,
    };
}
