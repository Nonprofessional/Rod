using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Webhooks;
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of the webhook subscription's own invariants (architecture.md
/// Sec 4.4): the declarative shape the forwarder interprets, the standing
/// boundaries (<see cref="WebhookLimits"/>), and the in-memory store the
/// forwarder reads through. The delivery path itself is the engine tests'
/// ground (Rod.Operators.Tests); these pin what a stored subscription may
/// be.
/// </summary>
public class WebhookSubscriptionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static WebhookSubscription Subscription(
        string url = "https://hooks.example.test/push",
        LiveEventKind[]? kinds = null)
        => WebhookSubscription.Create(
            WebhookSubscriptionId.New(),
            EngagementId.New(),
            "overnight-watch",
            url,
            kinds ?? [LiveEventKind.SessionOpened, LiveEventKind.ShellSessionOpened],
            Now,
            OperatorId.New());

    [Fact]
    public void Create_StoresTheShapeEnabled()
    {
        var subscription = Subscription();

        Assert.True(subscription.Enabled);
        Assert.Equal("overnight-watch", subscription.Name);
        Assert.Equal(
            [LiveEventKind.SessionOpened, LiveEventKind.ShellSessionOpened],
            subscription.EventKinds);
        Assert.True(subscription.IsNotifiedBy(LiveEventKind.SessionOpened));
        Assert.False(subscription.IsNotifiedBy(LiveEventKind.TaskIssued));
        Assert.Equal(0, subscription.DeliveryCount);
        Assert.Null(subscription.LastDeliveredAt);
    }

    [Fact]
    public void Create_DeduplicatesTheKindSelection()
    {
        var subscription = Subscription(kinds:
            [LiveEventKind.TaskCompleted, LiveEventKind.TaskCompleted]);

        Assert.Equal([LiveEventKind.TaskCompleted], subscription.EventKinds);
    }

    [Fact]
    public void Create_RejectsANamelessSubscription()
        => Assert.Throws<ArgumentException>(() => WebhookSubscription.Create(
            WebhookSubscriptionId.New(),
            EngagementId.New(),
            "  ",
            "https://hooks.example.test/push",
            [LiveEventKind.SessionOpened],
            Now,
            OperatorId.New()));

    [Fact]
    public void Create_RejectsAnEmptyKindSelection()
        => Assert.Throws<ArgumentException>(() => Subscription(kinds: []));

    [Theory]
    [InlineData(LiveEventKind.OperatorJoined)]
    [InlineData(LiveEventKind.OperatorLeft)]
    [InlineData(LiveEventKind.ChannelOutput)]
    public void Create_RejectsKindsOutsideTheNotifiableSet(LiveEventKind kind)
        => Assert.Throws<ArgumentException>(() => Subscription(kinds: [kind]));

    [Theory]
    [InlineData("ftp://hooks.example.test/push")]
    [InlineData("http://hooks.example.test/push")]
    [InlineData("https://user:pass@hooks.example.test/push")]
    [InlineData("https://hooks.example.test/push#fragment")]
    [InlineData("not-a-url")]
    [InlineData("")]
    [InlineData(null)]
    public void Create_RejectsUrlsOutsideThePosture(string? url)
        => Assert.Throws<ArgumentException>(() => Subscription(url: url!));

    [Theory]
    [InlineData("https://hooks.example.test/push")]
    [InlineData("https://hooks.example.test/push?token=sealed")]
    [InlineData("http://127.0.0.1:9/push")]
    [InlineData("http://[::1]/push")]
    [InlineData("http://localhost:9/push")]
    public void Create_AcceptsTheDocumentedUrlShapes(string url)
        => Assert.NotNull(Subscription(url: url));

    [Theory]
    [InlineData(LiveEventKind.SessionOpened)]
    [InlineData(LiveEventKind.SessionClosed)]
    [InlineData(LiveEventKind.TaskIssued)]
    [InlineData(LiveEventKind.TaskCompleted)]
    [InlineData(LiveEventKind.TaskCancelled)]
    [InlineData(LiveEventKind.ImplantRetired)]
    [InlineData(LiveEventKind.ShellSessionOpened)]
    [InlineData(LiveEventKind.ShellSessionEnded)]
    [InlineData(LiveEventKind.PayloadFetched)]
    public void OperationalBeats_AreNotifiable(LiveEventKind kind)
        => Assert.True(WebhookLimits.IsNotifiableKind(kind));

    [Fact]
    public void RecordDelivery_AdvancesTheCountAndResetsTheFailureRun()
    {
        var subscription = Subscription();
        subscription.RecordFailure(Now, "refused");

        var deliveredAt = Now + TimeSpan.FromMinutes(5);
        subscription.RecordDelivery(deliveredAt);

        Assert.Equal(1, subscription.DeliveryCount);
        Assert.Equal(deliveredAt, subscription.LastDeliveredAt);
        Assert.Equal(0, subscription.ConsecutiveFailures);
        Assert.Null(subscription.LastFailureReason);
    }

    [Fact]
    public void RecordFailure_ParksTheSubscriptionAtTheLimit()
    {
        var subscription = Subscription();

        Assert.False(subscription.RecordFailure(Now, "timeout"));
        Assert.False(subscription.RecordFailure(Now, "timeout"));
        Assert.True(subscription.RecordFailure(Now, "timeout"));

        Assert.False(subscription.Enabled);
        Assert.NotNull(subscription.DisabledAt);
        Assert.Equal("timeout", subscription.LastFailureReason);
        Assert.False(subscription.IsNotifiedBy(LiveEventKind.SessionOpened));
    }

    [Fact]
    public void DisableIsTheCancel_AndEnableStartsTheFailureRunFresh()
    {
        var subscription = Subscription();

        Assert.True(subscription.Disable(Now));
        Assert.False(subscription.Disable(Now)); // idempotent
        subscription.RecordFailure(Now, "refused");
        subscription.RecordFailure(Now, "refused");

        var rearmAt = Now + TimeSpan.FromHours(2);
        Assert.True(subscription.Enable(rearmAt));
        Assert.True(subscription.Enabled);
        Assert.Null(subscription.DisabledAt);
        Assert.Equal(0, subscription.ConsecutiveFailures);
        Assert.Null(subscription.LastFailureReason);
    }

    [Fact]
    public async Task Store_ScopesByEngagementAndKeepsTheEnabledScan()
    {
        var store = new InMemoryWebhookSubscriptionStore();
        var engagement = EngagementId.New();
        var other = EngagementId.New();
        var mine = WebhookSubscription.Create(
            WebhookSubscriptionId.New(), engagement, "mine",
            "https://hooks.example.test/a", [LiveEventKind.SessionOpened], Now, OperatorId.New());
        var theirs = WebhookSubscription.Create(
            WebhookSubscriptionId.New(), other, "theirs",
            "https://hooks.example.test/b", [LiveEventKind.TaskIssued], Now, OperatorId.New());
        var mineDisabled = WebhookSubscription.Create(
            WebhookSubscriptionId.New(), engagement, "parked",
            "https://hooks.example.test/c", [LiveEventKind.ImplantRetired], Now, OperatorId.New());
        mineDisabled.Disable(Now);

        await store.SaveAsync(mine);
        await store.SaveAsync(theirs);
        await store.SaveAsync(mineDisabled);

        var byEngagement = await store.ListByEngagementAsync(engagement);
        Assert.Equal(2, byEngagement.Count);
        Assert.All(byEngagement, s => Assert.Equal(engagement, s.EngagementId));

        var enabled = await store.ListEnabledAsync();
        Assert.Single(enabled, s => s.Id == mine.Id);

        Assert.NotNull(await store.FindAsync(mine.Id));
        Assert.True(await store.RemoveAsync(mine.Id));
        Assert.Null(await store.FindAsync(mine.Id));
        Assert.False(await store.RemoveAsync(mine.Id));
    }
}
