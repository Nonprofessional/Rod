using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Automation;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;

namespace Rod.Operators.Automation;

/// <summary>
/// The rule-management use cases (architecture.md Sec 10.4): create, list,
/// read, enable, disable, delete. Creation validates eagerly what it can --
/// the engagement open, the target implant present and in the engagement,
/// the verb unblocked and inside the class set -- so an operator's mistake
/// is a refusal at creation instead of a refusal audit three hours later.
/// The issuance gates at fire time (on <see cref="TaskService"/>) remain the
/// authority either way; this front door only refuses what could never fire.
/// Every mutation lands in the engagement trail attributed to the mutating
/// operator.
/// </summary>
public sealed class AutomationService
{
    private readonly IAutomationRuleStore _rules;
    private readonly IEngagementRepository _engagements;
    private readonly IImplantRepository _implants;
    private readonly ITaskCapabilityResolver _capabilities;
    private readonly ISensitiveVerbPolicy _sensitive;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _clock;

    public AutomationService(
        IAutomationRuleStore rules,
        IEngagementRepository engagements,
        IImplantRepository implants,
        ITaskCapabilityResolver capabilities,
        ISensitiveVerbPolicy sensitive,
        IAuditStore audit,
        TimeProvider clock)
    {
        _rules = rules;
        _engagements = engagements;
        _implants = implants;
        _capabilities = capabilities;
        _sensitive = sensitive;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>What the operator asked to persist (ids are typed; defaults resolve here).</summary>
    public sealed record CreateRuleCommand(
        EngagementId EngagementId,
        string Name,
        AutomationTrigger Trigger,
        ImplantId? OnlyImplant,
        string? CompletedVerb,
        ImplantId TargetImplant,
        string Verb,
        string Arguments,
        TimeSpan? Cooldown,
        int? MaxFirings,
        OperatorId CreatedBy);

    public async Task<AutomationRule> CreateAsync(CreateRuleCommand command, CancellationToken cancellationToken = default)
    {
        var engagement = await _engagements.FindAsync(command.EngagementId, cancellationToken)
            ?? throw new AutomationRuleRejectedException("Unknown engagement.");
        if (engagement.IsClosed || engagement.IsRetired)
            throw new AutomationRuleRejectedException("The engagement is closed; it accepts no new automation.");

        var existing = await _rules.ListByEngagementAsync(command.EngagementId, cancellationToken);
        if (existing.Count >= AutomationLimits.MaxRulesPerEngagement)
            throw new AutomationRuleRejectedException(
                $"The engagement already holds {existing.Count} automation rules (limit {AutomationLimits.MaxRulesPerEngagement}).");

        var implant = await ResolveTargetAsync(command.EngagementId, command.TargetImplant, cancellationToken);
        ValidateVerb(implant, command.Verb);
        await ValidateTriggerAsync(command.EngagementId, command.Trigger, command.CompletedVerb, command.OnlyImplant, cancellationToken);

        var cooldown = command.Cooldown ?? DefaultCooldown(command.Trigger);
        var rule = AutomationRule.Create(
            AutomationRuleId.New(),
            command.EngagementId,
            command.Name,
            command.Trigger,
            command.OnlyImplant,
            NormalizeCompletedVerb(command.Trigger, command.CompletedVerb),
            command.TargetImplant,
            command.Verb,
            command.Arguments ?? string.Empty,
            cooldown,
            command.MaxFirings ?? AutomationLimits.DefaultMaxFirings,
            _clock.GetUtcNow(),
            command.CreatedBy);

        await _rules.SaveAsync(rule, cancellationToken);
        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: rule.EngagementId.Value,
                operatorId: rule.CreatedBy.Value,
                implantId: rule.TargetImplant.Value,
                taskId: Guid.Empty,
                verb: rule.Verb,
                kind: AuditEventKind.AutomationRuleCreated,
                payload: $"created '{rule.Name}' {DescribeTrigger(rule)} -> {rule.Verb} on {rule.TargetImplant}",
                output: null,
                outcome: rule.Id.ToString(),
                at: rule.CreatedAt),
            cancellationToken);
        return rule;
    }

    public async Task<IReadOnlyList<AutomationRule>> ListAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
        => await _rules.ListByEngagementAsync(engagementId, cancellationToken);

    public async Task<AutomationRule?> FindAsync(
        EngagementId engagementId,
        AutomationRuleId id,
        CancellationToken cancellationToken = default)
    {
        var rule = await _rules.FindAsync(id, cancellationToken);
        return rule is { EngagementId: var scope } && scope == engagementId ? rule : null;
    }

    /// <summary>Re-arms the rule from now. Returns the stored rule; the state change is idempotent.</summary>
    public async Task<AutomationRule> EnableAsync(
        EngagementId engagementId,
        AutomationRuleId id,
        OperatorId changedBy,
        CancellationToken cancellationToken = default)
        => await TransitionAsync(engagementId, id, changedBy, enable: true, cancellationToken);

    /// <summary>The cancel: a disabled rule fires nothing until re-enabled. Idempotent.</summary>
    public async Task<AutomationRule> DisableAsync(
        EngagementId engagementId,
        AutomationRuleId id,
        OperatorId changedBy,
        CancellationToken cancellationToken = default)
        => await TransitionAsync(engagementId, id, changedBy, enable: false, cancellationToken);

    private async Task<AutomationRule> TransitionAsync(
        EngagementId engagementId,
        AutomationRuleId id,
        OperatorId changedBy,
        bool enable,
        CancellationToken cancellationToken)
    {
        var rule = await FindAsync(engagementId, id, cancellationToken)
            ?? throw new InvalidOperationException("The automation rule does not exist in this engagement.");

        var now = _clock.GetUtcNow();
        var changed = enable ? rule.Enable(now) : rule.Disable(now);
        if (!changed)
            return rule;

        await _rules.SaveAsync(rule, cancellationToken);
        await AppendTransitionAuditAsync(rule, changedBy, enable ? "operator-enabled" : "operator-disabled", now, cancellationToken);
        return rule;
    }

    /// <summary>Deletes the rule outright. Returns false when it was not stored in this engagement.</summary>
    public async Task<bool> DeleteAsync(
        EngagementId engagementId,
        AutomationRuleId id,
        OperatorId deletedBy,
        CancellationToken cancellationToken = default)
    {
        var rule = await FindAsync(engagementId, id, cancellationToken);
        if (rule is null)
            return false;

        await _rules.RemoveAsync(id, cancellationToken);
        var now = _clock.GetUtcNow();
        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: rule.EngagementId.Value,
                operatorId: deletedBy.Value,
                implantId: rule.TargetImplant.Value,
                taskId: Guid.Empty,
                verb: rule.Verb,
                kind: AuditEventKind.AutomationRuleDeleted,
                payload: $"deleted '{rule.Name}' {DescribeTrigger(rule)} -> {rule.Verb} on {rule.TargetImplant}",
                output: null,
                outcome: rule.Id.ToString(),
                at: now),
            cancellationToken);
        return true;
    }

    /// <summary>Appends an <see cref="AuditEventKind.AutomationRuleUpdated"/> fact naming the cause.</summary>
    public Task AppendTransitionAuditAsync(
        AutomationRule rule,
        OperatorId changedBy,
        string cause,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
        => _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: rule.EngagementId.Value,
                operatorId: changedBy.Value,
                implantId: rule.TargetImplant.Value,
                taskId: Guid.Empty,
                verb: rule.Verb,
                kind: AuditEventKind.AutomationRuleUpdated,
                payload: $"{cause} '{rule.Name}' ({DescribeTrigger(rule)} -> {rule.Verb} on {rule.TargetImplant})",
                output: null,
                outcome: rule.Id.ToString(),
                at: at),
            cancellationToken);

    private async Task<Implant> ResolveTargetAsync(EngagementId engagementId, ImplantId target, CancellationToken cancellationToken)
    {
        var implant = await _implants.FindAsync(target, cancellationToken)
            ?? throw new AutomationRuleRejectedException("Unknown target implant.");
        if (implant.EngagementId != engagementId)
            throw new AutomationRuleRejectedException("The target implant belongs to another engagement.");
        if (implant.IsRetired)
            throw new AutomationRuleRejectedException("The target implant is retired.");
        return implant;
    }

    private void ValidateVerb(Implant target, string? verb)
    {
        if (string.IsNullOrWhiteSpace(verb))
            throw new AutomationRuleRejectedException("A rule needs a verb.");
        if (Rod.CoreState.Tasks.ChannelVerbs.IsChannelVerb(verb))
            throw new AutomationRuleRejectedException(
                $"The verb '{verb}' runs as a live channel, and an unattended firing cannot own an interactive input half.");
        if (_sensitive.IsSensitive(verb))
            throw new AutomationRuleRejectedException(
                $"The verb '{verb}' is sensitive and never fires unattended; the sensitivity policy names it.");
        if (!_capabilities.IsDispatchable(target.Class, verb))
            throw new AutomationRuleRejectedException(
                $"The verb '{verb}' is outside the {target.Class} class's reduced verb set.");
    }

    private async Task ValidateTriggerAsync(
        EngagementId engagementId,
        AutomationTrigger trigger,
        string? completedVerb,
        ImplantId? onlyImplant,
        CancellationToken cancellationToken)
    {
        if (trigger is AutomationTrigger.Event(var kind))
        {
            if (!AutomationLimits.IsSupportedEventTrigger(kind))
                throw new AutomationRuleRejectedException($"The {kind} event is not triggerable.");
            if (completedVerb is not null && kind != LiveEventKind.TaskCompleted)
                throw new AutomationRuleRejectedException(
                    "The completed-verb condition applies only to task-completed triggers.");
        }

        if (onlyImplant is { } filter)
        {
            // A filter naming an implant outside the engagement can never
            // match -- refuse it at creation so the operator reads the typo
            // now, not in a quiet rule later.
            var implant = await _implants.FindAsync(filter, cancellationToken);
            if (implant is null || implant.EngagementId != engagementId)
                throw new AutomationRuleRejectedException("The event-filter implant belongs to another engagement.");
        }
    }

    private static TimeSpan DefaultCooldown(AutomationTrigger trigger)
        => trigger is AutomationTrigger.Interval interval
            ? interval.Every
            : AutomationLimits.DefaultEventCooldown;

    /// <summary>
    /// A completion condition is meaningless on non-completion triggers, so
    /// it is dropped there rather than carried as dead weight.
    /// </summary>
    private static string? NormalizeCompletedVerb(AutomationTrigger trigger, string? completedVerb)
        => trigger is AutomationTrigger.Event(var kind)
            && kind == LiveEventKind.TaskCompleted
            && !string.IsNullOrWhiteSpace(completedVerb)
                ? completedVerb.Trim()
                : null;

    internal static string DescribeTrigger(AutomationRule rule)
        => rule.Trigger switch
        {
            AutomationTrigger.Interval interval => $"every {interval.Every}",
            AutomationTrigger.Event(var kind) => rule.CompletedVerb is { } verb
                ? $"on {kind} of {verb}"
                : $"on {kind}",
            _ => string.Empty,
        };
}
