using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Implants;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// The notification acceptance criterion (architecture.md Sec 4.4): an
/// operator registers a webhook for session-opened and shell-caught
/// events, and a new contact delivers a push to it. The whole slice runs
/// against a real Kestrel teamserver -- registration through the operator
/// front, the forwarder's own bus subscription, and an implant contacting
/// over the real WebSocket beacon so the session-opened event is the one
/// the handshake path publishes, not one the test fakes.
/// </summary>
public class WebhookDeliveryTests
{
    [Fact]
    public async Task ANewContact_PushesToTheRegisteredWebhook()
    {
        await using var env = await TestEnv.StartAsync();
        using var receiver = LoopbackWebhook.Start();

        // Register for the overnight watch: sessions opening and shells
        // getting caught -- the shell kinds are named even though this leg
        // drives a contact; their delivery path is the engine's ground.
        var engagementId = await CreateEngagementAsync(env.Http);
        var register = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/webhook-subscriptions",
            new
            {
                Name = "overnight-watch",
                Url = receiver.Url,
                EventKinds = new[] { "SessionOpened", "ShellSessionOpened" },
            });
        register.EnsureSuccessStatusCode();

        // Attach the forwarder's pump before the contact so the push rides
        // the engine's real subscription (the tick is the reconcile, the
        // same one the hosted loop runs).
        await env.Host.Services.GetRequiredService<Rod.Operators.Webhooks.WebhookDeliveryEngine>()
            .TickOnceAsync();

        // A new contact: an implant over the real beacon stream. The
        // handshake opens its first session, which is exactly the event the
        // subscription named.
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        Assert.True(EngagementId.TryParse(engagementId, out var scope));
        var implant = Implant.Enroll(
            ImplantId.New(), scope, now.AddDays(30), ImplantClass.Implant, now);
        await implants.SaveAsync(implant);
        using var beacon = await WsBeaconClient.ConnectAsync(
            env.HttpPort, implant.Id.ToString(), new[] { "shell.exec" });
        var response = await beacon.ReceiveHandshakeAsync();
        Assert.Equal(HandshakeStatus.Ok, response.Status);

        // The push landed: the frame the console would have seen, at the
        // channel the operator registered.
        await receiver.UntilAsync(1);
        var (_, body) = receiver.Requests.Single();
        Assert.Contains("\"kind\":\"SessionOpened\"", body);
        Assert.Contains(engagementId, body);
        Assert.Contains(implant.Id.ToString(), body);

        // And the trail records the delivery beside the session it mirrored.
        // The receiver records the push the moment it answers; the audit
        // fact follows the same breath in the pump -- poll instead of
        // racing it.
        var delivered = await UntilAsync(async () =>
        {
            var audit = await env.Http.GetFromJsonAsync<AuditListBody>(
                $"/engagements/{engagementId}/audit?limit=100");
            return audit!.Items.FirstOrDefault(e =>
                e.Kind == "WebhookDelivered" && e.Outcome.StartsWith("delivered:"));
        });
        Assert.Contains("SessionOpened", delivered.Payload);

        // The session's own audit fact precedes the delivery beside it.
        var audit = await env.Http.GetFromJsonAsync<AuditListBody>(
            $"/engagements/{engagementId}/audit?limit=100");
        Assert.Contains(audit!.Items, e => e.Kind == "SessionOpened");

        // The channel's bookkeeping advanced through the store (the fact
        // lands after the save, so it is safe to read now).
        var subscriptions = await env.Http.GetFromJsonAsync<SubscriptionListBody>(
            $"/engagements/{engagementId}/webhook-subscriptions");
        Assert.Equal(1, Assert.Single(subscriptions!.Subscriptions).DeliveryCount);
    }

    /// <summary>Waits until the audit probe yields a value.</summary>
    private static async Task<AuditEntry> UntilAsync(Func<Task<AuditEntry?>> probe)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await probe() is { } found)
                return found;
            await Task.Delay(50);
        }

        Assert.Fail("The audit fact never landed.");
        return null!;
    }

    private static async Task<string> CreateEngagementAsync(HttpClient http)
    {
        var engagement = await http.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Watchtower"));
        engagement.EnsureSuccessStatusCode();
        return (await engagement.Content.ReadFromJsonAsync
            <EngagementEndpoints.EngagementResponse>())!.EngagementId;
    }

    /// <summary>
    /// A real Kestrel teamserver with the plain-HTTP operator API and the
    /// WebSocket beacon riding the same listener family.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            env.HttpPort = TestSupport.GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(env.HttpPort)))
                .Build();
            await env.Host.StartAsync();

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}"),
            };
            await AuthenticatedHost.LoginAsync(env.Http);
            return env;
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }

    private sealed class SubscriptionListBody
    {
        public SubscriptionBody[] Subscriptions { get; set; } = [];
    }

    private sealed class SubscriptionBody
    {
        public int DeliveryCount { get; set; }
    }

    private sealed class AuditListBody
    {
        public AuditEntry[] Items { get; set; } = [];
    }

    private sealed class AuditEntry
    {
        public string Kind { get; set; } = "";
        public string Payload { get; set; } = "";
        public string Outcome { get; set; } = "";
    }
}
