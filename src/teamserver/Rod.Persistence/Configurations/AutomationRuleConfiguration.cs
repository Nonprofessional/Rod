using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Automation;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the engagement-scoped automation rules
/// (architecture.md Sec 10.4). One row per rule with its trigger,
/// condition, action, and guard state flattened onto columns -- the
/// next-fire stamp among them, which is what makes a time trigger survive
/// a teamserver restart. A Persistence-owned mirror of core state's
/// <see cref="AutomationRule"/> aggregate (the domain stays free of any
/// stored shape); the trigger discriminated union becomes a kind column
/// plus the one payload column its kind reads.
/// </summary>
internal sealed class StoredAutomationRule
{
    public Guid Id { get; set; }
    public EngagementId EngagementId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>"interval" or "event".</summary>
    public string TriggerKind { get; set; } = string.Empty;

    /// <summary>The interval trigger's cadence, in whole seconds; null for event triggers.</summary>
    public long? IntervalSeconds { get; set; }

    /// <summary>The event trigger's live event kind name; null for interval triggers.</summary>
    public string? EventKind { get; set; }

    public ImplantId? OnlyImplantId { get; set; }
    public string? CompletedVerb { get; set; }
    public ImplantId TargetImplantId { get; set; }
    public string Verb { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;

    public long CooldownSeconds { get; set; }
    public int MaxFirings { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset? NextFireAt { get; set; }
    public int FireCount { get; set; }
    public DateTimeOffset? LastFiredAt { get; set; }
    public int ConsecutiveRefusals { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public OperatorId CreatedBy { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }

    public static StoredAutomationRule From(AutomationRule rule) => new()
    {
        Id = rule.Id.Value,
        EngagementId = rule.EngagementId,
        Name = rule.Name,
        TriggerKind = rule.Trigger is AutomationTrigger.Interval ? "interval" : "event",
        IntervalSeconds = rule.Trigger is AutomationTrigger.Interval interval ? (long)interval.Every.TotalSeconds : null,
        EventKind = rule.Trigger is AutomationTrigger.Event(var kind) ? kind.ToString() : null,
        OnlyImplantId = rule.OnlyImplant,
        CompletedVerb = rule.CompletedVerb,
        TargetImplantId = rule.TargetImplant,
        Verb = rule.Verb,
        Arguments = rule.Arguments,
        CooldownSeconds = (long)rule.Cooldown.TotalSeconds,
        MaxFirings = rule.MaxFirings,
        Enabled = rule.Enabled,
        NextFireAt = rule.NextFireAt,
        FireCount = rule.FireCount,
        LastFiredAt = rule.LastFiredAt,
        ConsecutiveRefusals = rule.ConsecutiveRefusals,
        CreatedAt = rule.CreatedAt,
        CreatedBy = rule.CreatedBy,
        DisabledAt = rule.DisabledAt,
    };

    public AutomationRule ToDomain() => new(
        new AutomationRuleId(Id),
        EngagementId,
        Name,
        Trigger(),
        OnlyImplantId,
        CompletedVerb,
        TargetImplantId,
        Verb,
        Arguments,
        TimeSpan.FromSeconds(CooldownSeconds),
        MaxFirings,
        Enabled,
        NextFireAt,
        FireCount,
        LastFiredAt,
        ConsecutiveRefusals,
        CreatedAt,
        CreatedBy,
        DisabledAt);

    private AutomationTrigger Trigger() => TriggerKind == "interval"
        ? new AutomationTrigger.Interval(TimeSpan.FromSeconds(IntervalSeconds ?? 0))
        : new AutomationTrigger.Event(Enum.Parse<LiveEventKind>(EventKind ?? nameof(LiveEventKind.SessionOpened)));
}

internal sealed class AutomationRuleConfiguration : IEntityTypeConfiguration<StoredAutomationRule>
{
    public void Configure(EntityTypeBuilder<StoredAutomationRule> builder)
    {
        builder.ToTable("automation_rules");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("automation_rule_id");

        builder.Property(r => r.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(200);
        builder.Property(r => r.TriggerKind).HasColumnName("trigger_kind").HasMaxLength(16);
        builder.Property(r => r.IntervalSeconds).HasColumnName("interval_seconds");
        builder.Property(r => r.EventKind).HasColumnName("event_kind").HasMaxLength(64);
        builder.Property(r => r.OnlyImplantId)
            .HasConversion(IdConverters.ImplantId)
            .HasColumnName("only_implant_id");
        builder.Property(r => r.CompletedVerb).HasColumnName("completed_verb").HasMaxLength(200);
        builder.Property(r => r.TargetImplantId)
            .HasConversion(IdConverters.ImplantId)
            .HasColumnName("target_implant_id");
        builder.Property(r => r.Verb).HasColumnName("verb").HasMaxLength(200);
        builder.Property(r => r.Arguments).HasColumnName("arguments");
        builder.Property(r => r.CooldownSeconds).HasColumnName("cooldown_seconds");
        builder.Property(r => r.MaxFirings).HasColumnName("max_firings");
        builder.Property(r => r.Enabled).HasColumnName("enabled");
        builder.Property(r => r.NextFireAt).HasColumnName("next_fire_at");
        builder.Property(r => r.FireCount).HasColumnName("fire_count");
        builder.Property(r => r.LastFiredAt).HasColumnName("last_fired_at");
        builder.Property(r => r.ConsecutiveRefusals).HasColumnName("consecutive_refusals");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
        builder.Property(r => r.CreatedBy)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("created_by");
        builder.Property(r => r.DisabledAt).HasColumnName("disabled_at");

        // The engine's tick scans enabled rules across engagements and the
        // operator API lists by engagement; both read through this index.
        builder.HasIndex(r => r.EngagementId).HasDatabaseName("ix_automation_rules_engagement_id");
        builder.HasIndex(r => r.Enabled).HasDatabaseName("ix_automation_rules_enabled");
    }
}
