using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
using Rod.CoreState.Webhooks;
using Rod.Operators.Live;
using Rod.Operators.Webhooks;
using Task = System.Threading.Tasks.Task;

namespace Rod.Operators.Tests;

/// <summary>
/// Checks of the webhook forwarder's delivery path (architecture.md
/// Sec 4.4): a matching event pushes the SSE frame shape through the
/// outbound client, the kind filter and the engagement scope hold, a
/// failing receiver parks its subscription after the consecutive limit,
/// and every attempt lands in the trail as a
/// <see cref="AuditEventKind.WebhookDelivered"/> fact that never names
/// the URL.
/// </summary>
public class WebhookDeliveryEngineTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void AdvanceTo(DateTimeOffset at) => _now = at;
    }

    /// <summary>
    /// The far end of the push: records every request verbatim and answers
    /// with a fixed status (200 unless the test says otherwise).
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(Uri Url, string Body)> Requests = new();
        public volatile int Status = 200;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Requests.Enqueue((request.RequestUri!, body));
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status));
        }
    }

    /// <summary>Serves the one handler-backed client the rig pushes with.</summary>
    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Rig
    {
        public readonly FakeTime Clock = new();
        public readonly InMemoryWebhookSubscriptionStore Subscriptions = new();
        public readonly InMemoryEngagementRepository Engagements = new();
        public readonly InMemoryAuditStore Audit = new();
        public readonly InMemoryLiveEventBus Bus = new();
        public readonly RecordingHandler Http = new();
        public readonly WebhookService Service;
        public readonly WebhookPusher Pusher;
        public readonly EngagementId Scope = EngagementId.New();
        public readonly EngagementId OtherScope = EngagementId.New();
        public readonly OperatorId Owner = OperatorId.New();

        public Rig()
        {
            Pusher = new WebhookPusher(new SingleClientFactory(new HttpClient(Http)));
            Service = new WebhookService(Subscriptions, Engagements, Audit, Pusher, Clock);

            foreach (var (scope, name) in new[] { (Scope, "watch"), (OtherScope, "other") })
            {
                var engagement = Engagement.Create(scope, name, Owner, DateTimeOffset.UnixEpoch);
                Engagements.SaveAsync(engagement).GetAwaiter().GetResult();
            }
        }

        public WebhookDeliveryEngine NewEngine()
            => new(
                Subscriptions,
                Bus,
                Pusher,
                Audit,
                Clock,
                Options.Create(new WebhookOptions()),
                NullLogger<WebhookDeliveryEngine>.Instance);

        public Task<WebhookSubscription> RegisterAsync(
            LiveEventKind[] kinds,
            string url = "https://hooks.example.test/push",
            EngagementId? scope = null)
            => Service.RegisterAsync(new WebhookService.RegisterCommand(
                scope ?? Scope, "watch", url, kinds, Owner));

        public Task PublishSessionOpenedAsync(EngagementId scope)
            => Bus.PublishAsync(LiveEvent.SessionOpened(scope, Owner, ImplantId.New(), "2.1", Start));

        public Task PublishShellCaughtAsync(EngagementId scope)
            => Bus.PublishAsync(LiveEvent.ShellSession(
                scope, LiveEventKind.ShellSessionOpened, """{"sessionId":"s1"}""", Start));

        public Task PublishTaskIssuedAsync(EngagementId scope)
            => Bus.PublishAsync(LiveEvent.TaskIssued(
                scope, Owner, ImplantId.New(), TaskId.New(), "shell.exec uptime", Start));

        public async Task<IReadOnlyList<AuditEvent>> TrailAsync(EngagementId scope)
            => await Audit.ListAsync(scope.Value, CancellationToken.None);

        public Task TickAsync(WebhookDeliveryEngine engine)
            => engine.TickOnceAsync();

        /// <summary>Waits for the engine's background pump to land its effect.</summary>
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
    public async Task AMatchingEvent_PushesTheFrameShapeAndRecordsTheTrail()
    {
        var rig = new Rig();
        await rig.RegisterAsync([LiveEventKind.SessionOpened, LiveEventKind.ShellSessionOpened]);
        var engine = rig.NewEngine();
        await rig.TickAsync(engine);

        await rig.PublishSessionOpenedAsync(rig.Scope);
        await rig.UntilAsync(() => Task.FromResult(rig.Http.Requests.Count == 1));

        var (_, body) = rig.Http.Requests.Single();
        var frame = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("SessionOpened", frame.GetProperty("kind").GetString());
        Assert.Equal(rig.Scope.ToString(), frame.GetProperty("engagementId").GetString());
        Assert.Equal("2.1", frame.GetProperty("payload").GetString());
        Assert.Equal(Start, frame.GetProperty("at").GetDateTimeOffset());

        var trail = await rig.TrailAsync(rig.Scope);
        var fact = Assert.Single(trail, e => e.Kind == AuditEventKind.WebhookDelivered);
        Assert.Equal("delivered:200", fact.Outcome);
        Assert.DoesNotContain("hooks.example.test", fact.Payload, StringComparison.Ordinal);

        var stored = await rig.Subscriptions.ListByEngagementAsync(rig.Scope);
        Assert.Equal(1, Assert.Single(stored).DeliveryCount);
    }

    [Fact]
    public async Task AShellCaughtEvent_PushesToTheSubscriptionThatNamedIt()
    {
        var rig = new Rig();
        await rig.RegisterAsync([LiveEventKind.SessionOpened, LiveEventKind.ShellSessionOpened]);
        var engine = rig.NewEngine();
        await rig.TickAsync(engine);

        await rig.PublishShellCaughtAsync(rig.Scope);
        await rig.UntilAsync(() => Task.FromResult(rig.Http.Requests.Count == 1));

        var (_, body) = rig.Http.Requests.Single();
        var frame = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("ShellSessionOpened", frame.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ANonMatchingKind_PushesNothing()
    {
        var rig = new Rig();
        await rig.RegisterAsync([LiveEventKind.SessionOpened]);
        var engine = rig.NewEngine();
        await rig.TickAsync(engine);

        await rig.PublishTaskIssuedAsync(rig.Scope);
        await Task.Delay(200);

        Assert.Empty(rig.Http.Requests);
        Assert.DoesNotContain(await rig.TrailAsync(rig.Scope),
            e => e.Kind == AuditEventKind.WebhookDelivered);
    }

    [Fact]
    public async Task ADisabledSubscription_PushesNothing()
    {
        var rig = new Rig();
        var subscription = await rig.RegisterAsync([LiveEventKind.SessionOpened]);
        await rig.Service.DisableAsync(rig.Scope, subscription.Id, rig.Owner);
        var engine = rig.NewEngine();
        await rig.TickAsync(engine);

        await rig.PublishSessionOpenedAsync(rig.Scope);
        await Task.Delay(200);

        Assert.Empty(rig.Http.Requests);
    }

    [Fact]
    public async Task EventsStayEngagementScoped()
    {
        var rig = new Rig();
        await rig.RegisterAsync([LiveEventKind.SessionOpened], url: "https://hooks.example.test/mine");
        await rig.RegisterAsync(
            [LiveEventKind.SessionOpened], url: "https://hooks.example.test/theirs", scope: rig.OtherScope);
        var engine = rig.NewEngine();
        await rig.TickAsync(engine);

        await rig.PublishSessionOpenedAsync(rig.Scope);
        await rig.UntilAsync(() => Task.FromResult(rig.Http.Requests.Count == 1));
        await Task.Delay(200);

        var (url, _) = rig.Http.Requests.Single();
        Assert.Equal("https://hooks.example.test/mine", url.ToString());
        Assert.DoesNotContain(await rig.TrailAsync(rig.OtherScope),
            e => e.Kind == AuditEventKind.WebhookDelivered);
    }

    [Fact]
    public async Task ConsecutiveFailures_ParkTheSubscriptionWithTheCauseInTheTrail()
    {
        var rig = new Rig();
        rig.Http.Status = 500;
        await rig.RegisterAsync([LiveEventKind.SessionOpened]);
        var engine = rig.NewEngine();
        await rig.TickAsync(engine);

        for (var i = 0; i < WebhookLimits.ConsecutiveFailureLimit; i++)
        {
            await rig.PublishSessionOpenedAsync(rig.Scope);
            await rig.UntilAsync(() => Task.FromResult(rig.Http.Requests.Count == i + 1));
        }

        var stored = await rig.Subscriptions.ListByEngagementAsync(rig.Scope);
        var parked = Assert.Single(stored);
        Assert.False(parked.Enabled);
        Assert.Equal($"HTTP {500}", parked.LastFailureReason);

        var trail = await rig.TrailAsync(rig.Scope);
        Assert.Equal(WebhookLimits.ConsecutiveFailureLimit,
            trail.Count(e => e.Kind == AuditEventKind.WebhookDelivered && e.Outcome == "failed:500"));
        var cause = Assert.Single(trail, e => e.Kind == AuditEventKind.WebhookSubscriptionUpdated);
        Assert.Contains("auto-disabled", cause.Payload, StringComparison.Ordinal);

        // A parked subscription stays quiet even when more matching events fire.
        await rig.PublishSessionOpenedAsync(rig.Scope);
        await Task.Delay(200);
        Assert.Equal(WebhookLimits.ConsecutiveFailureLimit, rig.Http.Requests.Count);
    }

    [Fact]
    public async Task ASubscriptionRegisteredAfterATick_IsHeardOnTheNextOne()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await rig.TickAsync(engine); // no subscriptions yet: nothing to subscribe

        await rig.RegisterAsync([LiveEventKind.SessionOpened]);
        await rig.TickAsync(engine);
        await rig.PublishSessionOpenedAsync(rig.Scope);
        await rig.UntilAsync(() => Task.FromResult(rig.Http.Requests.Count == 1));
    }

    [Fact]
    public async Task ATestPush_RidesTheSamePathWithAKindNoRealEventCarries()
    {
        var rig = new Rig();
        var subscription = await rig.RegisterAsync([LiveEventKind.SessionOpened]);

        var result = await rig.Service.TestDeliverAsync(rig.Scope, subscription.Id, rig.Owner);

        Assert.True(result.Delivered);
        var (_, body) = rig.Http.Requests.Single();
        var frame = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(WebhookPusher.TestKind, frame.GetProperty("kind").GetString());
        Assert.Equal(rig.Scope.ToString(), frame.GetProperty("engagementId").GetString());

        var trail = await rig.TrailAsync(rig.Scope);
        var fact = Assert.Single(trail, e => e.Kind == AuditEventKind.WebhookDelivered);
        Assert.Equal("tested:delivered:200", fact.Outcome);
    }
}
