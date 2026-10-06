using Rod.CoreState.Automation;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of the automation rule's own invariants (architecture.md
/// Sec 10.4): the declarative shape the engine interprets, the standing
/// boundaries (<see cref="AutomationLimits"/>), and the in-memory store the
/// engine reads through. The firing path itself is the engine tests' ground
/// (Rod.Operators.Tests); these pin what a stored rule may be.
/// </summary>
public class AutomationRuleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static AutomationRule IntervalRule(TimeSpan every, TimeSpan? cooldown = null, int maxFirings = 100)
        => AutomationRule.Create(
            AutomationRuleId.New(),
            EngagementId.New(),
            "watch",
            new AutomationTrigger.Interval(every),
            onlyImplant: null,
            completedVerb: null,
            ImplantId.New(),
            "shell.exec",
            "uptime",
            cooldown ?? every,
            maxFirings,
            Now,
            OperatorId.New());

    [Fact]
    public void Create_ArmsAnIntervalRuleOneCadenceOut()
    {
        var rule = IntervalRule(TimeSpan.FromMinutes(30));

        Assert.True(rule.Enabled);
        Assert.Equal(Now + TimeSpan.FromMinutes(30), rule.NextFireAt);
        Assert.False(rule.IsDue(Now + TimeSpan.FromMinutes(29)));
        Assert.True(rule.IsDue(Now + TimeSpan.FromMinutes(30)));
        Assert.True(rule.CooldownPassed(Now));
        Assert.Equal(0, rule.FireCount);
    }

    [Fact]
    public void Create_ArmsAnEventRuleWithoutASchedule()
    {
        var rule = AutomationRule.Create(
            AutomationRuleId.New(),
            EngagementId.New(),
            "first-contact",
            new AutomationTrigger.Event(LiveEventKind.SessionOpened),
            null,
            null,
            ImplantId.New(),
            "recon.ps",
            "",
            TimeSpan.FromMinutes(1),
            10,
            Now,
            OperatorId.New());

        Assert.Null(rule.NextFireAt);
        Assert.False(rule.IsDue(Now.AddHours(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(31)]
    public void Create_RejectsOutOfBoundIntervals(int days)
    {
        var outOfBounds = days switch
        {
            0 => TimeSpan.FromSeconds(4),
            4 => TimeSpan.FromSeconds(4),
            _ => TimeSpan.FromDays(31),
        };

        Assert.Throws<ArgumentException>(() => IntervalRule(outOfBounds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void Create_RejectsOutOfBoundShape(int maxFirings)
        => Assert.Throws<ArgumentException>(() => IntervalRule(TimeSpan.FromMinutes(30), maxFirings: maxFirings));

    [Fact]
    public void Create_RejectsANamelessRule()
        => Assert.Throws<ArgumentException>(() => AutomationRule.Create(
            AutomationRuleId.New(),
            EngagementId.New(),
            "  ",
            new AutomationTrigger.Interval(TimeSpan.FromMinutes(30)),
            null,
            null,
            ImplantId.New(),
            "shell.exec",
            "",
            TimeSpan.FromMinutes(30),
            100,
            Now,
            OperatorId.New()));

    [Fact]
    public void RecordFire_AdvancesTheStampAndResetsTheRefusalStreak()
    {
        var rule = IntervalRule(TimeSpan.FromMinutes(30));
        rule.RecordRefusal(Now);

        var firedAt = Now + TimeSpan.FromMinutes(30);
        rule.RecordFire(firedAt);

        Assert.Equal(1, rule.FireCount);
        Assert.Equal(firedAt, rule.LastFiredAt);
        Assert.Equal(firedAt + TimeSpan.FromMinutes(30), rule.NextFireAt);
        Assert.Equal(0, rule.ConsecutiveRefusals);
        Assert.True(rule.CooldownPassed(firedAt + TimeSpan.FromMinutes(30)));
        Assert.False(rule.CooldownPassed(firedAt + TimeSpan.FromMinutes(29)));
    }

    [Fact]
    public void RecordRefusal_WaitsOutTheIntervalInsteadOfRetrying()
    {
        var rule = IntervalRule(TimeSpan.FromMinutes(30));
        var refusedAt = Now + TimeSpan.FromMinutes(30);

        rule.RecordRefusal(refusedAt);

        Assert.Equal(1, rule.ConsecutiveRefusals);
        Assert.Equal(refusedAt + TimeSpan.FromMinutes(30), rule.NextFireAt);
        Assert.False(rule.IsDue(refusedAt.AddSeconds(1)));
    }

    [Fact]
    public void Disable_IsTheCancelAndEnable_RearmsFromNow()
    {
        var rule = IntervalRule(TimeSpan.FromMinutes(30));

        Assert.True(rule.Disable(Now));
        Assert.False(rule.Enabled);
        Assert.Null(rule.NextFireAt);
        Assert.False(rule.Disable(Now)); // idempotent

        var rearmAt = Now + TimeSpan.FromHours(2);
        Assert.True(rule.Enable(rearmAt));
        Assert.True(rule.Enabled);
        Assert.Equal(rearmAt + TimeSpan.FromMinutes(30), rule.NextFireAt);
    }

    [Fact]
    public void CapReached_DisablesTheRuleTheMomentItIsSpent()
    {
        var rule = IntervalRule(TimeSpan.FromMinutes(30), maxFirings: 2);
        rule.RecordFire(Now);
        Assert.False(rule.CapReached);

        rule.RecordFire(Now + TimeSpan.FromMinutes(30));
        Assert.True(rule.CapReached);
    }

    [Theory]
    [InlineData("shell.interact")]
    [InlineData("TUNNEL.FORWARD")]
    [InlineData("tunnel.socks")]
    [InlineData("inject.shellcode")]
    [InlineData("collect.minidump")]
    [InlineData("COLLECT.KEYLOG")]
    [InlineData("evasion.avoid")]
    [InlineData("exploit.module")]
    [InlineData("evasion.something.new")]
    [InlineData("")]
    [InlineData(null)]
    public void BlockedVerbs_NeverFireUnattended(string? verb)
        => Assert.True(AutomationLimits.IsBlockedVerb(verb));

    [Theory]
    [InlineData("shell.exec")]
    [InlineData("file.pull")]
    [InlineData("collect.screenshot")]
    [InlineData("collect.cred")]
    [InlineData("recon.ps")]
    [InlineData("persist.install")]
    [InlineData("lateral.move")]
    [InlineData("exfil.push")]
    [InlineData("beacon.sleep")]
    public void ReferenceVerbs_StayAutomatable(string verb)
        => Assert.False(AutomationLimits.IsBlockedVerb(verb));

    [Theory]
    [InlineData(LiveEventKind.SessionOpened)]
    [InlineData(LiveEventKind.SessionClosed)]
    [InlineData(LiveEventKind.TaskIssued)]
    [InlineData(LiveEventKind.TaskCompleted)]
    [InlineData(LiveEventKind.TaskCancelled)]
    [InlineData(LiveEventKind.ImplantRetired)]
    public void OperationalBeats_AreTriggerable(LiveEventKind kind)
        => Assert.True(AutomationLimits.IsSupportedEventTrigger(kind));

    [Theory]
    [InlineData(LiveEventKind.OperatorJoined)]
    [InlineData(LiveEventKind.OperatorLeft)]
    [InlineData(LiveEventKind.ChannelOutput)]
    [InlineData(LiveEventKind.ShellSessionOpened)]
    [InlineData(LiveEventKind.PayloadFetched)]
    public void ConsoleChatter_AndTheChunkFirehose_AreNotTriggerable(LiveEventKind kind)
        => Assert.False(AutomationLimits.IsSupportedEventTrigger(kind));

    [Fact]
    public async Task Store_ScopesByEngagementAndKeepsTheEnabledScan()
    {
        var store = new InMemoryAutomationRuleStore();
        var engagement = EngagementId.New();
        var other = EngagementId.New();
        var mine = IntervalRule(TimeSpan.FromMinutes(30));
        var theirs = IntervalRule(TimeSpan.FromMinutes(30));
        var mineDisabled = IntervalRule(TimeSpan.FromMinutes(30));

        var now = DateTimeOffset.UnixEpoch;
        var mineRow = AutomationRule.Create(
            mine.Id, engagement, mine.Name, mine.Trigger, null, null, mine.TargetImplant,
            mine.Verb, mine.Arguments, mine.Cooldown, mine.MaxFirings, now, mine.CreatedBy);
        var theirsRow = AutomationRule.Create(
            theirs.Id, other, theirs.Name, theirs.Trigger, null, null, theirs.TargetImplant,
            theirs.Verb, theirs.Arguments, theirs.Cooldown, theirs.MaxFirings, now, theirs.CreatedBy);
        var disabledRow = AutomationRule.Create(
            mineDisabled.Id, engagement, mineDisabled.Name, mineDisabled.Trigger, null, null,
            mineDisabled.TargetImplant, mineDisabled.Verb, mineDisabled.Arguments,
            mineDisabled.Cooldown, mineDisabled.MaxFirings, now, mineDisabled.CreatedBy);
        disabledRow.Disable(now);

        await store.SaveAsync(mineRow);
        await store.SaveAsync(theirsRow);
        await store.SaveAsync(disabledRow);

        var byEngagement = await store.ListByEngagementAsync(engagement);
        Assert.Equal(2, byEngagement.Count);
        Assert.All(byEngagement, r => Assert.Equal(engagement, r.EngagementId));

        var enabled = await store.ListEnabledAsync();
        var enabledRule = Assert.Single(enabled, r => r.Id == mine.Id);
        Assert.NotNull(enabledRule);

        Assert.NotNull(await store.FindAsync(mine.Id));
        Assert.True(await store.RemoveAsync(mine.Id));
        Assert.Null(await store.FindAsync(mine.Id));
        Assert.False(await store.RemoveAsync(mine.Id));
    }
}
