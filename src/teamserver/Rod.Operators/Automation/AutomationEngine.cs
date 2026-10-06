using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Automation;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
using Task = System.Threading.Tasks.Task;

namespace Rod.Operators.Automation;

// The domain's task entity shadows the BCL name; the engine speaks the BCL
// one throughout and reaches the entity only through the repository port.

/// <summary>
/// The automation engine (architecture.md Sec 10.4): one hosted background
/// service that turns declarative rules into tasking while no operator
/// watches. Two paths lead to the same firing routine -- a fixed-delay tick
/// scanning due time rules, and a per-engagement subscription on the live
/// event bus for event rules -- and every firing rides
/// <see cref="TaskService.IssueAsync"/> unchanged, attributed to the
/// synthetic automation operator, with its audit arc written through the
/// same on-issued hook the transport endpoints use.
///
/// Guards live in the firing routine, under a serialization gate: the
/// cooldown and the firing cap are read-and-advanced atomically with respect
/// to other firings, a burst of matching events yields one firing, and a
/// task the engine itself issued never matches an event trigger (chain
/// depth one -- automation chains terminate by construction). Refusals are
/// heard, not retried in a loop: each lands in the trail, and three in a
/// row disable the rule with the cause named.
///
/// The bus's posture carries: event triggers are best-effort (process-local,
/// drop-oldest, no replay), and the engine's causality map is process-local
/// with them. Time triggers are durable through the rule's persisted
/// next-fire stamp -- this process dying costs at most the firing in
/// flight, never the schedule.
/// </summary>
public sealed class AutomationEngine : BackgroundService
{
    /// <summary>
    /// The causality map's bound: past this many tracked firings it resets.
    /// A rule's cap is at most a thousand and the map only needs to outlive
    /// a chain, so a reset costs at most one extra link, never a loop.
    /// </summary>
    private const int CausalityMapLimit = 4096;

    private readonly IAutomationRuleStore _rules;
    private readonly TaskService _tasks;
    private readonly ITaskRepository _taskRecords;
    private readonly IAuditStore _audit;
    private readonly IOperatorRepository _operators;
    private readonly ILiveEventBus _bus;
    private readonly AutomationService _service;
    private readonly TimeProvider _clock;
    private readonly AutomationOptions _options;
    private readonly ILogger<AutomationEngine> _logger;

    private readonly SemaphoreSlim _fireGate = new(1, 1);
    private readonly ConcurrentDictionary<TaskId, byte> _automationIssued = new();
    private readonly object _subscriptionLock = new();
    private readonly Dictionary<EngagementId, CancellationTokenSource> _subscriptions = new();

    public AutomationEngine(
        IAutomationRuleStore rules,
        TaskService tasks,
        ITaskRepository taskRecords,
        IAuditStore audit,
        IOperatorRepository operators,
        ILiveEventBus bus,
        AutomationService service,
        TimeProvider clock,
        IOptions<AutomationOptions> options,
        ILogger<AutomationEngine> logger)
    {
        _rules = rules;
        _tasks = tasks;
        _taskRecords = taskRecords;
        _audit = audit;
        _operators = operators;
        _bus = bus;
        _service = service;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SeedAutomationOperatorAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seeding the automation operator failed; firings still attribute to its well-known id.");
        }

        var tick = TimeSpan.FromSeconds(Math.Max(1, _options.EngineTickSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed tick costs one scan, never the engine: the loop
                // rides on and the next tick re-reads the store.
                _logger.LogError(ex, "Automation tick failed.");
            }

            try
            {
                await Task.Delay(tick, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        RetireSubscriptions();
    }

    /// <summary>
    /// One scan: fire every due time rule, then reconcile the per-engagement
    /// event subscriptions against the enabled event rules. Public because
    /// the tests drive it directly -- a tick is a pure scan of the store, so
    /// the loop around it is timing and nothing else.
    /// </summary>
    public async Task TickOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var enabled = await _rules.ListEnabledAsync(cancellationToken);

        foreach (var rule in enabled)
        {
            if (rule.Trigger is not AutomationTrigger.Interval)
                continue;

            if (rule.CapReached)
            {
                // A capped rule disables itself the moment it is noticed --
                // the stamp says due, the count says spent.
                await GuardDisableAsync(rule, "cap-reached", cancellationToken);
                continue;
            }

            if (rule.IsDue(now))
                await FireAsync(rule, "interval", cancellationToken);
        }

        var eventEngagements = enabled
            .Where(r => r.Trigger is AutomationTrigger.Event)
            .Select(r => r.EngagementId)
            .Distinct()
            .ToArray();
        ReconcileSubscriptions(eventEngagements);
    }

    private async Task FireAsync(AutomationRule rule, string cause, CancellationToken cancellationToken)
    {
        // Firings serialize: the cooldown and cap are read-and-advanced as
        // one step against every other firing, so a burst of matching
        // events (or a tick racing an event) yields one firing, not a storm.
        await _fireGate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.GetUtcNow();
            if (!rule.Enabled)
                return;
            if (rule.CapReached)
            {
                await GuardDisableAsync(rule, "cap-reached", cancellationToken);
                return;
            }
            if (!rule.CooldownPassed(now))
                return;

            try
            {
                var issued = await _tasks.IssueAsync(
                    new IssueTaskCommand(
                        rule.EngagementId,
                        rule.TargetImplant,
                        AutomationOperatorIdentity.OperatorId,
                        rule.Verb,
                        rule.Arguments),
                    onIssued: (taskIssued, ct) => AppendIssuedAuditAsync(taskIssued, ct),
                    cancellationToken: cancellationToken);

                TrackCausality(issued.TaskId);
                rule.RecordFire(now);
                await _rules.SaveAsync(rule, cancellationToken);
                await AppendFiredAuditAsync(rule, cause, issued.TaskId.ToString(), now, cancellationToken);
                if (rule.CapReached)
                    await GuardDisableAsync(rule, "cap-reached", cancellationToken);
            }
            catch (TaskRejectedException ex)
            {
                // A refused firing is part of the engagement's story: the
                // ROE refusal audit the transport path writes has its engine
                // twin here, then the rule records the refusal and waits out
                // its interval -- never a tight retry loop.
                if (ex.Reason == TaskRejectionReason.RoeViolation)
                {
                    await AppendRoeRefusedAuditAsync(rule, now, cancellationToken);
                }

                rule.RecordRefusal(now);
                var disable = rule.ConsecutiveRefusals >= AutomationLimits.ConsecutiveRefusalLimit;
                if (disable)
                    rule.Disable(now);
                await _rules.SaveAsync(rule, cancellationToken);
                await AppendFiredAuditAsync(rule, cause, $"refused:{ex.Reason}", now, cancellationToken);
                if (disable)
                    await _service.AppendTransitionAuditAsync(rule, AutomationOperatorIdentity.OperatorId, "auto-disabled (repeated refusals)", now, cancellationToken);
                _logger.LogWarning(
                    "Automation rule {Rule} firing refused ({Reason}); {Streak} consecutive.",
                    rule.Id, ex.Reason, rule.ConsecutiveRefusals);
            }
        }
        finally
        {
            _fireGate.Release();
        }
    }

    private async Task HandleEventAsync(EngagementId engagement, LiveEvent @event, CancellationToken cancellationToken)
    {
        // Chain depth one: a completed or issued task that was itself an
        // automation firing never re-triggers. The operator-driven half of
        // the chain -- a human's task completing, automation following up --
        // carries no automation task id and fires normally.
        if (@event.TaskId is { } caused && _automationIssued.ContainsKey(caused))
            return;

        var rules = await _rules.ListEnabledAsync(cancellationToken);
        foreach (var rule in rules)
        {
            if (rule.EngagementId != engagement)
                continue;
            if (rule.Trigger is not AutomationTrigger.Event(var kind) || kind != @event.Kind)
                continue;
            if (rule.OnlyImplant is { } filter && @event.ImplantId != filter)
                continue;
            if (rule.CompletedVerb is { } verb && !await MatchesCompletedVerbAsync(@event, verb, cancellationToken))
                continue;

            await FireAsync(rule, $"event {@event.Kind}", cancellationToken);
        }
    }

    private async Task<bool> MatchesCompletedVerbAsync(LiveEvent @event, string verb, CancellationToken cancellationToken)
    {
        if (@event.Kind != LiveEventKind.TaskCompleted || @event.TaskId is not { } taskId)
            return false;

        // The completion event carries output, not the verb; the task record
        // is the attribution-true source for which verb completed.
        var task = await _taskRecords.FindAsync(taskId, cancellationToken);
        return task is not null
            && string.Equals(task.Verb, verb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One subscription per engagement holding enabled event rules. The
    /// reconcile runs every tick, so a rule created a minute ago is already
    /// subscribed; draining is sequential per engagement -- a slow firing
    /// delays later events for the engine only, never for an operator's SSE
    /// stream (each subscriber owns its channel).
    /// </summary>
    private void ReconcileSubscriptions(IReadOnlyCollection<EngagementId> engagements)
    {
        lock (_subscriptionLock)
        {
            foreach (var retired in _subscriptions.Keys.Except(engagements).ToArray())
            {
                if (_subscriptions.Remove(retired, out var cts))
                    cts.Cancel();
            }

            foreach (var added in engagements.Except(_subscriptions.Keys))
            {
                var cts = new CancellationTokenSource();
                _subscriptions[added] = cts;
                _ = PumpAsync(added, cts);
            }
        }
    }

    private async Task PumpAsync(EngagementId engagement, CancellationTokenSource subscription)
    {
        try
        {
            await foreach (var @event in _bus.SubscribeAsync(engagement, subscription.Token))
            {
                await HandleEventAsync(engagement, @event, subscription.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The subscription was retired; nothing left to drain.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automation event pump for engagement {Engagement} ended; the next tick re-subscribes.", engagement);
        }
        finally
        {
            subscription.Dispose();
        }
    }

    private void RetireSubscriptions()
    {
        lock (_subscriptionLock)
        {
            foreach (var cts in _subscriptions.Values)
                cts.Cancel();
            _subscriptions.Clear();
        }
    }

    private async Task GuardDisableAsync(AutomationRule rule, string cause, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (rule.Disable(now))
        {
            await _rules.SaveAsync(rule, cancellationToken);
            await GuardDisableAuditAsync(rule, cause, now, cancellationToken);
        }
    }

    private Task GuardDisableAuditAsync(AutomationRule rule, string cause, DateTimeOffset at, CancellationToken cancellationToken)
        => _service.AppendTransitionAuditAsync(rule, AutomationOperatorIdentity.OperatorId, $"auto-disabled ({cause})", at, cancellationToken);

    private void TrackCausality(TaskId task)
    {
        if (_automationIssued.Count >= CausalityMapLimit)
            _automationIssued.Clear();
        _automationIssued.TryAdd(task, 0);
    }

    /// <summary>
    /// The task-issuance audit fact, shaped exactly like the transport
    /// endpoint's -- same kind, same fields -- so a fired task's trail is
    /// indistinguishable in shape from an operator-issued one, and the
    /// operator id is what tells them apart.
    /// </summary>
    private Task AppendIssuedAuditAsync(TaskIssued issued, CancellationToken cancellationToken)
        => _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: issued.EngagementId.Value,
                operatorId: issued.IssuedBy.Value,
                implantId: issued.ImplantId.Value,
                taskId: issued.TaskId.Value,
                verb: issued.Verb,
                kind: AuditEventKind.TaskIssued,
                payload: issued.Arguments,
                output: null,
                outcome: issued.TaskId.ToString(),
                at: issued.CreatedAt),
            cancellationToken);

    private Task AppendFiredAuditAsync(
        AutomationRule rule,
        string cause,
        string outcome,
        DateTimeOffset at,
        CancellationToken cancellationToken)
        => _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: rule.EngagementId.Value,
                operatorId: AutomationOperatorIdentity.OperatorId.Value,
                implantId: rule.TargetImplant.Value,
                taskId: Guid.Empty,
                verb: rule.Verb,
                kind: AuditEventKind.AutomationRuleFired,
                payload: $"'{rule.Name}' ({rule.Id}) on {cause}",
                output: null,
                outcome: outcome,
                at: at),
            cancellationToken);

    private Task AppendRoeRefusedAuditAsync(AutomationRule rule, DateTimeOffset at, CancellationToken cancellationToken)
        => _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: rule.EngagementId.Value,
                operatorId: AutomationOperatorIdentity.OperatorId.Value,
                implantId: rule.TargetImplant.Value,
                taskId: Guid.Empty,
                verb: rule.Verb,
                kind: AuditEventKind.TaskRoeRefused,
                payload: rule.Arguments,
                output: null,
                outcome: "automation: outside engagement ROE",
                at: at),
            cancellationToken);

    private async Task SeedAutomationOperatorAsync(CancellationToken cancellationToken)
    {
        var existing = await _operators.FindByHandleAsync(AutomationOperatorIdentity.Handle, cancellationToken);
        if (existing is not null)
            return;

        var automation = Operator.Register(
            AutomationOperatorIdentity.OperatorId,
            AutomationOperatorIdentity.Handle,
            AutomationOperatorIdentity.DisplayName,
            _clock.GetUtcNow());
        await _operators.SaveAsync(automation, cancellationToken);
        _logger.LogInformation("Seeded the synthetic automation operator '{Handle}'.", AutomationOperatorIdentity.Handle);
    }
}
