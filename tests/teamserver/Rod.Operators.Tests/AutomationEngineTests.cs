using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Automation;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
using Rod.Operators.Automation;
using Rod.Operators.Live;
using Task = System.Threading.Tasks.Task;

namespace Rod.Operators.Tests;

/// <summary>
/// Checks of the automation engine's firing path (architecture.md
/// Sec 10.4): a due interval rule and a matching event both issue through
/// <see cref="TaskService"/> attributed to the synthetic automation
/// operator, the guards (cooldown, cap, refusal streak, chain depth) hold,
/// and the schedule rides the rule itself -- a second engine instance over
/// the same stores resumes it, the in-memory twin of the restart the
/// Postgres durability test exercises end to end.
/// </summary>
public class AutomationEngineTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void AdvanceTo(DateTimeOffset at) => _now = at;
    }

    private sealed class Rig
    {
        public readonly FakeTime Clock = new();
        public readonly InMemoryAutomationRuleStore Rules = new();
        public readonly InMemoryEngagementRepository Engagements = new();
        public readonly InMemoryImplantRepository Implants = new();
        public readonly InMemoryTaskRepository Tasks = new();
        public readonly InMemoryAuditStore Audit = new();
        public readonly InMemoryLiveEventBus Bus = new();
        public readonly TaskService TaskService;
        public readonly AutomationService Service;
        public readonly EngagementId Scope = EngagementId.New();
        public readonly Implant Implant;
        public readonly OperatorId Owner = OperatorId.New();

        public Rig(string? roeVerbPermit = null)
        {
            TaskService = new TaskService(Tasks, Implants, Engagements, Clock, Bus);
            Service = new AutomationService(
                Rules, Engagements, Implants, new ClassTableCapabilityResolver(),
                new DefaultSensitiveVerbPolicy(), Audit, Clock);

            var engagement = Rod.CoreState.Engagements.Engagement.Create(Scope, "watch", Owner, DateTimeOffset.UnixEpoch);
            if (roeVerbPermit is not null)
            {
                engagement.ApplyRoe(new RoeProfile([roeVerbPermit], []));
            }
            Engagements.SaveAsync(engagement).GetAwaiter().GetResult();

            Implant = Rod.CoreState.Implants.Implant.Enroll(
                ImplantId.New(), Scope, Start.AddDays(30), ImplantClass.Implant, Start);
            Implants.SaveAsync(Implant).GetAwaiter().GetResult();
        }

        public AutomationEngine NewEngine()
            => new(
                Rules,
                TaskService,
                Tasks,
                Audit,
                new InMemoryOperatorRepository(),
                Bus,
                Service,
                Clock,
                Options.Create(new AutomationOptions()),
                NullLogger<AutomationEngine>.Instance);

        public Task<AutomationRule> AddIntervalRuleAsync(
            string verb = "shell.exec",
            string arguments = "uptime",
            TimeSpan? every = null,
            int maxFirings = 100,
            ImplantId? target = null)
            => Service.CreateAsync(new AutomationService.CreateRuleCommand(
                Scope,
                "watch",
                new AutomationTrigger.Interval(every ?? TimeSpan.FromMinutes(5)),
                null,
                null,
                target ?? Implant.Id,
                verb,
                arguments,
                Cooldown: null,
                maxFirings,
                Owner));

        public Task<AutomationRule> AddEventRuleAsync(
            LiveEventKind kind,
            ImplantId? onlyImplant = null,
            string? completedVerb = null,
            string verb = "shell.exec")
            => Service.CreateAsync(new AutomationService.CreateRuleCommand(
                Scope,
                "on-" + kind,
                new AutomationTrigger.Event(kind),
                onlyImplant,
                completedVerb,
                Implant.Id,
                verb,
                "uptime",
                Cooldown: null,
                MaxFirings: 100,
                Owner));

        public Task TickAsync(AutomationEngine engine)
            => engine.TickOnceAsync();

        public async Task<IReadOnlyList<AuditEvent>> TrailAsync()
            => await Audit.ListAsync(Scope.Value, CancellationToken.None);

        /// <summary>Waits for the engine's background event pump to land its effect.</summary>
        public async Task UntilAsync(Func<Task<bool>> condition)
        {
            for (var i = 0; i < 100; i++)
            {
                if (await condition())
                    return;
                await Task.Delay(50);
            }
            Assert.Fail("Condition not met within the wait window.");
        }
    }

    [Fact]
    public async Task ADueIntervalRule_FiresThroughTaskServiceAsAutomation()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        var rule = await rig.AddIntervalRuleAsync();

        await engine.TickOnceAsync(); // armed one cadence out: nothing yet
        Assert.Empty(await rig.Tasks.ListByImplantAsync(rig.Implant.Id));

        rig.Clock.AdvanceTo(Start + TimeSpan.FromMinutes(5));
        await engine.TickOnceAsync();

        var queued = await rig.Tasks.ListByImplantAsync(rig.Implant.Id);
        var task = Assert.Single(queued);
        Assert.Equal("shell.exec", task.Verb);
        Assert.Equal("uptime", task.Arguments);
        Assert.Equal(AutomationOperatorIdentity.OperatorId, task.IssuedBy); // automation attribution

        var trail = await rig.TrailAsync();
        var fired = Assert.Single(trail, e => e.Kind == AuditEventKind.AutomationRuleFired);
        Assert.Equal(AutomationOperatorIdentity.OperatorId.Value, fired.OperatorId);
        Assert.Equal(task.Id.ToString(), fired.Outcome);
        var issuedFact = Assert.Single(trail, e => e.Kind == AuditEventKind.TaskIssued);
        Assert.Equal(AutomationOperatorIdentity.OperatorId.Value, issuedFact.OperatorId);
        Assert.Equal(task.Id.Value, issuedFact.TaskId);

        var stored = await rig.Rules.FindAsync(rule.Id);
        Assert.Equal(1, stored!.FireCount);
        Assert.Equal(Start + TimeSpan.FromMinutes(10), stored.NextFireAt);
    }

    [Fact]
    public async Task AnIntervalRule_HoldsItsCadenceAcrossTicks()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await rig.AddIntervalRuleAsync();

        rig.Clock.AdvanceTo(Start + TimeSpan.FromMinutes(5));
        await engine.TickOnceAsync();
        rig.Clock.AdvanceTo(Start + TimeSpan.FromMinutes(9));
        await engine.TickOnceAsync(); // not due again yet
        var count = (await rig.Tasks.ListByImplantAsync(rig.Implant.Id)).Count;
        Assert.Equal(1, count);

        rig.Clock.AdvanceTo(Start + TimeSpan.FromMinutes(10));
        await engine.TickOnceAsync();
        count = (await rig.Tasks.ListByImplantAsync(rig.Implant.Id)).Count;
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ASecondEngineInstance_ResumesThePersistedSchedule()
    {
        var rig = new Rig();
        var first = rig.NewEngine();
        var rule = await rig.AddIntervalRuleAsync();

        rig.Clock.AdvanceTo(Start + TimeSpan.FromMinutes(5));
        await first.TickOnceAsync();
        Assert.Single(await rig.Tasks.ListByImplantAsync(rig.Implant.Id));

        // "Restart": a fresh engine over the same stores -- the rule, with
        // its next-fire stamp, is the only state that carries.
        rig.Clock.AdvanceTo(Start + TimeSpan.FromMinutes(10));
        var second = rig.NewEngine();
        await second.TickOnceAsync();

        var queued = await rig.Tasks.ListByImplantAsync(rig.Implant.Id);
        Assert.Equal(2, queued.Count);
        var stored = await rig.Rules.FindAsync(rule.Id);
        Assert.Equal(2, stored!.FireCount);
    }

    [Fact]
    public async Task AnEventRule_FiresOnItsKindAndHonorsTheImplantFilter()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        var other = Rod.CoreState.Implants.Implant.Enroll(
            ImplantId.New(), rig.Scope, Start.AddDays(30), ImplantClass.Implant, Start);
        await rig.Implants.SaveAsync(other);
        await rig.AddEventRuleAsync(LiveEventKind.SessionOpened, onlyImplant: rig.Implant.Id);

        await engine.TickOnceAsync(); // subscribes the engagement

        await rig.Bus.PublishAsync(LiveEvent.SessionOpened(
            rig.Scope, rig.Owner, other.Id, "1.0", rig.Clock.GetUtcNow()));
        await rig.UntilAsync(async () =>
            (await rig.Rules.ListByEngagementAsync(rig.Scope)).All(r => r.FireCount == 0));
        // the filtered-out implant never fired the rule; now the matching one does

        await rig.Bus.PublishAsync(LiveEvent.SessionOpened(
            rig.Scope, rig.Owner, rig.Implant.Id, "1.0", rig.Clock.GetUtcNow()));
        await rig.UntilAsync(async () =>
            (await rig.Rules.ListByEngagementAsync(rig.Scope)).Any(r => r.FireCount == 1));

        var queued = await rig.Tasks.ListByImplantAsync(rig.Implant.Id);
        Assert.Single(queued);
    }

    [Fact]
    public async Task ACompletionRule_FiresOnlyForItsVerb()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await rig.AddEventRuleAsync(LiveEventKind.TaskCompleted, completedVerb: "recon.ps");

        // An operator-issued task of a different verb completes: no fire.
        var shell = await rig.TaskService.IssueAsync(
            new IssueTaskCommand(rig.Scope, rig.Implant.Id, rig.Owner, "shell.exec", "id"));
        await engine.TickOnceAsync(); // subscribe
        await rig.Bus.PublishAsync(LiveEvent.TaskCompleted(
            rig.Scope, rig.Owner, rig.Implant.Id, shell.TaskId, "done", rig.Clock.GetUtcNow()));
        await rig.UntilAsync(async () =>
            (await rig.Rules.ListByEngagementAsync(rig.Scope)).All(r => r.FireCount == 0));

        // The matching verb completes: the follow-up fires.
        var recon = await rig.TaskService.IssueAsync(
            new IssueTaskCommand(rig.Scope, rig.Implant.Id, rig.Owner, "recon.ps", ""));
        await rig.Bus.PublishAsync(LiveEvent.TaskCompleted(
            rig.Scope, rig.Owner, rig.Implant.Id, recon.TaskId, "done", rig.Clock.GetUtcNow()));
        await rig.UntilAsync(async () =>
            (await rig.Rules.ListByEngagementAsync(rig.Scope)).Any(r => r.FireCount == 1));
    }

    [Fact]
    public async Task AutomationsOwnCompletion_NeverRetriggers()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await rig.AddIntervalRuleAsync(every: TimeSpan.FromMinutes(5));
        await rig.AddEventRuleAsync(LiveEventKind.TaskCompleted);

        rig.Clock.AdvanceTo(Start + TimeSpan.FromMinutes(5));
        await engine.TickOnceAsync(); // interval rule fires -> automation task exists

        var automationTask = Assert.Single(await rig.Tasks.ListByImplantAsync(rig.Implant.Id));
        await engine.TickOnceAsync(); // ensure subscribed before the events

        // Depth zero: an operator's task completing fires the follow-up.
        var operatorTask = await rig.TaskService.IssueAsync(
            new IssueTaskCommand(rig.Scope, rig.Implant.Id, rig.Owner, "recon.ps", ""));
        await rig.Bus.PublishAsync(LiveEvent.TaskCompleted(
            rig.Scope, rig.Owner, rig.Implant.Id, operatorTask.TaskId, "done", rig.Clock.GetUtcNow()));
        await rig.UntilAsync(async () =>
            (await rig.Rules.ListByEngagementAsync(rig.Scope))
                .Any(r => r.Trigger is AutomationTrigger.Event && r.FireCount == 1));

        // Depth one: the completion of the engine's own task never
        // re-triggers. The pump drains FIFO, so the guarded event has been
        // seen once the wait window passes; the count staying put is the
        // chain guard, not a slow pump.
        await rig.Bus.PublishAsync(LiveEvent.TaskCompleted(
            rig.Scope, AutomationOperatorIdentity.OperatorId, rig.Implant.Id,
            automationTask.Id, "done", rig.Clock.GetUtcNow()));
        await Task.Delay(300);

        var eventRule = (await rig.Rules.ListByEngagementAsync(rig.Scope))
            .Single(r => r.Trigger is AutomationTrigger.Event);
        Assert.Equal(1, eventRule.FireCount);

        // Three tasks on the implant: the interval firing, the operator's
        // manual recon.ps, and the one follow-up. A broken chain guard would
        // make the follow-up count two.
        var verbs = (await rig.Tasks.ListByImplantAsync(rig.Implant.Id))
            .Select(t => t.Verb)
            .OrderBy(v => v)
            .ToArray();
        Assert.Equal(["recon.ps", "shell.exec", "shell.exec"], verbs);
    }

    [Fact]
    public async Task TheCooldown_AbsorbsAnEventBurst()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await rig.AddEventRuleAsync(LiveEventKind.SessionOpened); // default cooldown 1 min

        await engine.TickOnceAsync();
        for (var i = 0; i < 5; i++)
        {
            await rig.Bus.PublishAsync(LiveEvent.SessionOpened(
                rig.Scope, rig.Owner, rig.Implant.Id, "1.0", rig.Clock.GetUtcNow()));
        }

        await rig.UntilAsync(async () =>
            (await rig.Rules.ListByEngagementAsync(rig.Scope)).Any(r => r.FireCount == 1));
        await Task.Delay(300); // let any stray second firing land

        var queued = await rig.Tasks.ListByImplantAsync(rig.Implant.Id);
        Assert.Single(queued);
    }

    [Fact]
    public async Task RefusedFirings_AreAudited_AndDisableTheRuleAfterThree()
    {
        var rig = new Rig(roeVerbPermit: "recon.ps"); // shell.exec outside ROE
        var engine = rig.NewEngine();
        var rule = await rig.AddIntervalRuleAsync(); // fires shell.exec

        for (var round = 1; round <= 3; round++)
        {
            rig.Clock.AdvanceTo(Start + TimeSpan.FromMinutes(5) * round);
            await engine.TickOnceAsync();
            var stored = await rig.Rules.FindAsync(rule.Id);
            Assert.Equal(round, stored!.ConsecutiveRefusals);
        }

        var trail = await rig.TrailAsync();
        Assert.Equal(3, trail.Count(e => e.Kind == AuditEventKind.AutomationRuleFired
            && e.Outcome.StartsWith("refused:", StringComparison.Ordinal)));
        Assert.Contains(trail, e => e.Kind == AuditEventKind.AutomationRuleUpdated
            && e.Payload.Contains("refusals", StringComparison.Ordinal));

        var disabled = await rig.Rules.FindAsync(rule.Id);
        Assert.False(disabled!.Enabled);

        rig.Clock.AdvanceTo(Start + TimeSpan.FromHours(1));
        await engine.TickOnceAsync();
        Assert.Equal(3, (await rig.TrailAsync()).Count(e => e.Kind == AuditEventKind.AutomationRuleFired));
    }

    [Fact]
    public async Task TheFiringCap_DisablesTheRuleWhenSpent()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        var rule = await rig.AddIntervalRuleAsync(maxFirings: 1);

        rig.Clock.AdvanceTo(Start + TimeSpan.FromMinutes(5));
        await engine.TickOnceAsync();

        var stored = await rig.Rules.FindAsync(rule.Id);
        Assert.Equal(1, stored!.FireCount);
        Assert.False(stored.Enabled); // spent: disabled by its own guard

        var trail = await rig.TrailAsync();
        Assert.Contains(trail, e => e.Kind == AuditEventKind.AutomationRuleUpdated
            && e.Payload.Contains("cap-reached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuleCreation_RefusesWhatCanNeverFire()
    {
        var rig = new Rig();

        await Assert.ThrowsAsync<AutomationRuleRejectedException>(() =>
            rig.AddIntervalRuleAsync(verb: "tunnel.forward"));
        await Assert.ThrowsAsync<AutomationRuleRejectedException>(() =>
            rig.AddIntervalRuleAsync(verb: "collect.keylog"));
        await Assert.ThrowsAsync<AutomationRuleRejectedException>(() =>
            rig.AddIntervalRuleAsync(verb: "evasion.avoid"));
        await Assert.ThrowsAsync<AutomationRuleRejectedException>(() =>
            rig.AddEventRuleAsync(LiveEventKind.OperatorJoined)); // console chatter is not triggerable
    }

    [Fact]
    public async Task RuleCreation_RefusesAClassForeignVerbAndAForeignImplant()
    {
        var rig = new Rig();

        // A WebShell-class implant runs shell.exec only.
        var shell = Rod.CoreState.Implants.Implant.Enroll(
            ImplantId.New(), rig.Scope, Start.AddDays(30), ImplantClass.WebShell, Start);
        await rig.Implants.SaveAsync(shell);
        await Assert.ThrowsAsync<AutomationRuleRejectedException>(() =>
            rig.AddIntervalRuleAsync(verb: "file.pull", target: shell.Id));

        // An implant from another engagement is not addressable at all.
        var outsider = Rod.CoreState.Implants.Implant.Enroll(
            ImplantId.New(), EngagementId.New(), Start.AddDays(30), ImplantClass.Implant, Start);
        await rig.Implants.SaveAsync(outsider);
        await Assert.ThrowsAsync<AutomationRuleRejectedException>(() =>
            rig.AddIntervalRuleAsync(target: outsider.Id));
    }
}
