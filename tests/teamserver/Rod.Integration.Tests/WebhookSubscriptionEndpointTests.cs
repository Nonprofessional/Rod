using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Rod.CoreState;
using Rod.CoreState.Live;
using Rod.Operators.Webhooks;
using Rod.Transport.Endpoints;
using Task = System.Threading.Tasks.Task;
using static Rod.Integration.Tests.EngagementSetup;

namespace Rod.Integration.Tests;

/// <summary>
/// The webhook-subscription operator surface (architecture.md Sec 4.4):
/// channels live behind the operator front's authentication, manage
/// through the engagement-scoped routes, refuse at registration what could
/// never push, the <c>:test</c> action verifies a channel against a
/// loopback receiver, and a live event on the bus reaches the registered
/// channel through the hosted forwarder.
/// </summary>
public class WebhookSubscriptionEndpointTests
{
    [Fact]
    public async Task Channels_ManageThroughTheOperatorApi()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var register = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/webhook-subscriptions",
                new
                {
                    Name = "overnight-watch",
                    Url = "https://hooks.example.test/push?token=sealed",
                    EventKinds = new[] { "SessionOpened", "ShellSessionOpened" },
                });
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
            var subscription = await register.Content.ReadFromJsonAsync<SubscriptionBody>();
            Assert.NotNull(subscription);
            Assert.True(subscription!.Enabled);
            Assert.Equal(2, subscription.EventKinds.Length);

            var fetched = await client.GetFromJsonAsync<SubscriptionBody>(
                $"/engagements/{engagementId}/webhook-subscriptions/{subscription.Id}");
            Assert.Equal("overnight-watch", fetched!.Name);

            var listed = await client.GetFromJsonAsync<SubscriptionListBody>(
                $"/engagements/{engagementId}/webhook-subscriptions");
            Assert.Single(listed!.Subscriptions);

            var disable = await client.PostAsync(
                $"/engagements/{engagementId}/webhook-subscriptions/{subscription.Id}:disable", content: null);
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
            var disabled = await disable.Content.ReadFromJsonAsync<SubscriptionBody>();
            Assert.False(disabled!.Enabled);

            var enable = await client.PostAsync(
                $"/engagements/{engagementId}/webhook-subscriptions/{subscription.Id}:enable", content: null);
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
            Assert.True((await enable.Content.ReadFromJsonAsync<SubscriptionBody>())!.Enabled);

            var deleted = await client.DeleteAsync(
                $"/engagements/{engagementId}/webhook-subscriptions/{subscription.Id}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            var empty = await client.GetFromJsonAsync<SubscriptionListBody>(
                $"/engagements/{engagementId}/webhook-subscriptions");
            Assert.Empty(empty!.Subscriptions);
        }
    }

    [Fact]
    public async Task Register_RefusesWhatCouldNeverPush()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            async Task<HttpStatusCode> RegisterAsync(object body)
            {
                var response = await client.PostAsJsonAsync(
                    $"/engagements/{engagementId}/webhook-subscriptions", body);
                return response.StatusCode;
            }

            var good = new { Name = "x", Url = "https://hooks.example.test/push", EventKinds = new[] { "SessionOpened" } };

            // The firehose is not notifiable and an off-posture URL is
            // refused; an empty kind selection is shape-incomplete, and an
            // unknown kind string is malformed -- both 400, not refusals.
            Assert.Equal(HttpStatusCode.UnprocessableEntity, await RegisterAsync(new
            {
                Name = "x",
                Url = "https://hooks.example.test/push",
                EventKinds = new[] { "ChannelOutput" },
            }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, await RegisterAsync(new
            {
                Name = "x",
                Url = "http://hooks.example.test/push",
                EventKinds = new[] { "SessionOpened" },
            }));
            Assert.Equal(HttpStatusCode.BadRequest, await RegisterAsync(new
            {
                Name = "x",
                Url = "https://hooks.example.test/push",
                EventKinds = Array.Empty<string>(),
            }));
            Assert.Equal(HttpStatusCode.BadRequest, await RegisterAsync(new
            {
                Name = "x",
                Url = "https://hooks.example.test/push",
                EventKinds = new[] { "NotAKind" },
            }));
            var foreignEngagement = await client.PostAsJsonAsync(
                $"/engagements/{Guid.NewGuid()}/webhook-subscriptions",
                good);
            Assert.Equal(HttpStatusCode.NotFound, foreignEngagement.StatusCode);
        }
    }

    [Fact]
    public async Task ATestPush_VerifiesTheChannelBeforeTheOperatorReliesOnIt()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        using (var receiver = LoopbackWebhook.Start())
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var register = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/webhook-subscriptions",
                new
                {
                    Name = "overnight-watch",
                    Url = receiver.Url,
                    EventKinds = new[] { "SessionOpened" },
                });
            register.EnsureSuccessStatusCode();
            var subscription = await register.Content.ReadFromJsonAsync<SubscriptionBody>();

            var test = await client.PostAsync(
                $"/engagements/{engagementId}/webhook-subscriptions/{subscription!.Id}:test", content: null);
            Assert.Equal(HttpStatusCode.OK, test.StatusCode);
            var outcome = await test.Content.ReadFromJsonAsync<TestBody>();
            Assert.True(outcome!.Delivered);

            await receiver.UntilAsync(1);
            var (_, body) = receiver.Requests.Single();
            Assert.Contains("\"kind\":\"test\"", body);
            Assert.Contains(engagementId, body);

            var audit = await client.GetFromJsonAsync<AuditListBody>(
                $"/engagements/{engagementId}/audit?limit=50");
            var created = Assert.Single(audit!.Items, e => e.Kind == "WebhookSubscriptionCreated");
            Assert.Equal(subscription.Id, created.Outcome);
            var tested = Assert.Single(audit.Items, e => e.Kind == "WebhookDelivered");
            Assert.StartsWith("tested:", tested.Outcome);
            // The URL is the channel's bearer secret; the trail never records it.
            Assert.DoesNotContain(receiver.Url, tested.Payload, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ALiveEventOnTheBus_ReachesTheRegisteredChannel()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        using (var receiver = LoopbackWebhook.Start())
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var register = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/webhook-subscriptions",
                new
                {
                    Name = "overnight-watch",
                    Url = receiver.Url,
                    EventKinds = new[] { "SessionOpened", "ShellSessionOpened" },
                });
            register.EnsureSuccessStatusCode();
            var subscription = await register.Content.ReadFromJsonAsync<SubscriptionBody>();

            // Drive the hosted forwarder's reconcile, then the bus: the
            // pump is the engine's own subscription, not this test's.
            var engine = host.Services.GetRequiredService<WebhookDeliveryEngine>();
            await engine.TickOnceAsync();
            var bus = host.Services.GetRequiredService<ILiveEventBus>();
            Assert.True(EngagementId.TryParse(engagementId, out var scope));
            await bus.PublishAsync(LiveEvent.ShellSession(
                scope, LiveEventKind.ShellSessionOpened, """{"sessionId":"s1"}""", DateTimeOffset.UtcNow));

            await receiver.UntilAsync(1);
            var (_, body) = receiver.Requests.Single();
            Assert.Contains("\"kind\":\"ShellSessionOpened\"", body);
            Assert.Contains(engagementId, body);

            var audit = await client.GetFromJsonAsync<AuditListBody>(
                $"/engagements/{engagementId}/audit?limit=50");
            var delivered = Assert.Single(audit!.Items, e => e.Kind == "WebhookDelivered" && e.Outcome.StartsWith("delivered:"));
            Assert.Contains("ShellSessionOpened", delivered.Payload);

            // The push updated the channel's bookkeeping through the store.
            var stored = await client.GetFromJsonAsync<SubscriptionBody>(
                $"/engagements/{engagementId}/webhook-subscriptions/{subscription!.Id}");
            Assert.Equal(1, stored!.DeliveryCount);
        }
    }

    private sealed class SubscriptionBody
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string[] EventKinds { get; set; } = [];
        public bool Enabled { get; set; }
        public int DeliveryCount { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    private sealed class SubscriptionListBody
    {
        public SubscriptionBody[] Subscriptions { get; set; } = [];
    }

    private sealed class TestBody
    {
        public bool Delivered { get; set; }
        public string Outcome { get; set; } = "";
    }

    private sealed class AuditListBody
    {
        public AuditEntry[] Items { get; set; } = [];
    }

    private sealed class AuditEntry
    {
        public string Kind { get; set; } = "";
        public Guid TaskId { get; set; }
        public string Payload { get; set; } = "";
        public string Outcome { get; set; } = "";
    }
}
