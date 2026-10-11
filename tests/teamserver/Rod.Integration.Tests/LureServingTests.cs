using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Campaigns;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;

namespace Rod.Integration.Tests;

/// <summary>
/// The lure serving edge (architecture.md Sec 11.5): the public route a
/// campaign recipient's message points at. The unguessable id is the only
/// credential; the artifact serve on click, the pixel's open, the
/// pre-build 404, the revoked campaign's 404, and the shared-tier refusal
/// all land here, with every serve writing its audit fact and evidence
/// stamp.
/// </summary>
public class LureServingTests
{
    // --- The in-memory half: the serve, the evidence, the 404 shapes. The
    //     harness socket is unknown to the listener registry, which stays
    //     permissive -- exactly the shape these paths need. ---

    private sealed record InMemoryHarness(
        HttpClient Client,
        IHost Host,
        EngagementId Engagement,
        ICampaignStore Campaigns,
        Campaign Campaign,
        CampaignRecipient Recipient);

    private static async Task<InMemoryHarness> NewAsync(CampaignState state = CampaignState.Launched)
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var created = await client.PostAsJsonAsync("/engagements", new { name = "lure" });
        created.EnsureSuccessStatusCode();
        var engagement = await created.Content.ReadFromJsonAsync<EngagementBody>();
        Assert.True(EngagementId.TryParse(engagement!.EngagementId, out var engagementId));

        var campaigns = host.Services.GetRequiredService<ICampaignStore>();
        var recipient = new CampaignRecipient(CampaignRecipientId.New(), "target@example.com", "Target", Guid.NewGuid());
        var campaign = new Campaign(
            CampaignId.New(), engagementId, "lure-run", operatorId, DateTimeOffset.UtcNow,
            "relay.example", 587, CampaignRelayTls.StartTls, null, null, "sender@example.com",
            "Subject", "Open {{link}}", false, "{}", Guid.NewGuid(), [recipient]);
        await campaigns.SaveAsync(campaign);
        if (state == CampaignState.Launched)
            await campaigns.LaunchAsync(campaign.Id, engagementId, DateTimeOffset.UtcNow);
        if (state == CampaignState.Revoked)
            await campaigns.RevokeAsync(campaign.Id, engagementId, DateTimeOffset.UtcNow);

        return new InMemoryHarness(client, host, engagementId, campaigns, campaign, recipient);
    }

    private static async Task<Guid> StorePayloadAndBindAsync(InMemoryHarness h)
    {
        var payloads = h.Host.Services.GetRequiredService<IPayloadStore>();
        var payloadId = Guid.NewGuid();
        await payloads.SaveAsync(new PayloadRecord(
            payloadId, h.Engagement.Value, "Implant", "rust", "application/octet-stream",
            "fingerprint", "stub-artifact-bytes"u8.ToArray(), 17, DateTimeOffset.UtcNow,
            Target: "linux/amd64", Endpoint: "https://front.example", TokenId: Guid.NewGuid()));

        // The build landed: the recipient's row now names the artifact.
        await h.Campaigns.NoteBuildingAsync(h.Campaign.Id, h.Recipient.Id,
            new DeployTokenId(Guid.NewGuid()), Guid.NewGuid());
        await h.Campaigns.NoteSentAsync(h.Campaign.Id, h.Recipient.Id, payloadId, DateTimeOffset.UtcNow);
        return payloadId;
    }

    private static string LurePath(InMemoryHarness h, string suffix = "")
        => $"/implants/lures/{h.Recipient.LureId:N}{suffix}";

    [Fact]
    public async Task LureClick_ServesTheRecipientArtifact_AndStampsEvidence()
    {
        var h = await NewAsync();
        await StorePayloadAndBindAsync(h);

        var response = await h.Client.GetAsync(LurePath(h));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", string.Join(",", response.Headers.GetValues("Cache-Control")));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("stub-artifact-bytes", System.Text.Encoding.UTF8.GetString(bytes));

        // The click stamped once and the audit fact carries the wire view.
        var readBack = (await h.Campaigns.FindAsync(h.Campaign.Id))!.Recipients[0];
        Assert.NotNull(readBack.ClickedAt);
        var audit = h.Host.Services.GetRequiredService<IAuditStore>();
        var facts = await audit.ListAsync(h.Engagement.Value);
        var served = facts.Single(f => f.Kind == AuditEventKind.CampaignLinkServed);
        Assert.Contains("click=served", served.Payload);
        Assert.Contains("recipient=target@example.com", served.Payload);
    }

    [Fact]
    public async Task LureClick_BeforeTheBuildLands_404sButRecords()
    {
        var h = await NewAsync();

        var response = await h.Client.GetAsync(LurePath(h));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The interest is evidence even when there was nothing to serve.
        var audit = h.Host.Services.GetRequiredService<IAuditStore>();
        var facts = await audit.ListAsync(h.Engagement.Value);
        Assert.Contains(facts, f => f.Kind == AuditEventKind.CampaignLinkServed && f.Payload.Contains("click=no-artifact"));
    }

    [Fact]
    public async Task Pixel_Open_ServesTheGif_AndStampsOpened()
    {
        var h = await NewAsync();

        var response = await h.Client.GetAsync(LurePath(h, "/open"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/gif", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", string.Join(",", response.Headers.GetValues("Cache-Control")));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(42, bytes.Length);

        var readBack = (await h.Campaigns.FindAsync(h.Campaign.Id))!.Recipients[0];
        Assert.NotNull(readBack.OpenedAt);

        var audit = h.Host.Services.GetRequiredService<IAuditStore>();
        var facts = await audit.ListAsync(h.Engagement.Value);
        Assert.Contains(facts, f => f.Kind == AuditEventKind.CampaignLinkServed && f.Payload.Contains("open=served"));
    }

    [Fact]
    public async Task RevokedCampaign_LureIsIndistinguishableFromGone()
    {
        var h = await NewAsync(state: CampaignState.Revoked);

        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(LurePath(h))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(LurePath(h, "/open"))).StatusCode);

        // And nothing recorded: a revoked campaign's edge answers as if the
        // lure never existed.
        var audit = h.Host.Services.GetRequiredService<IAuditStore>();
        var facts = await audit.ListAsync(h.Engagement.Value);
        Assert.DoesNotContain(facts, f => f.Kind == AuditEventKind.CampaignLinkServed);
    }

    // --- The real-socket half: the listener tiers. The engagement's own
    //     front serves the lure; the shared operator front refuses it --
    //     the same refusal the enroll and payload fetch routes run. ---

    [Fact]
    public async Task Lure_ServesOnTheEngagementListener_AndRefusesTheOperatorFront()
    {
        await using var env = await LureEnv.StartAsync();
        var engagementId = await CreateEngagementAsync(env.Http);
        await env.CreateEngagementListenerAsync(engagementId);

        // A live campaign with its artifact built, straight through the
        // store -- the endpoint layer above this one drives the same rows.
        var campaigns = env.Host.Services.GetRequiredService<ICampaignStore>();
        var recipient = new CampaignRecipient(
            CampaignRecipientId.New(), "target@example.com", null, Guid.NewGuid());
        var campaign = new Campaign(
            CampaignId.New(), new EngagementId(Guid.Parse(engagementId)), "live-run",
            AuthenticatedHost.GetOperatorId(env.Host), DateTimeOffset.UtcNow,
            "relay.example", 587, CampaignRelayTls.StartTls, null, null, "sender@example.com",
            "Subject", "Open {{link}}", false, "{}", Guid.NewGuid(), [recipient]);
        await campaigns.SaveAsync(campaign);
        await campaigns.LaunchAsync(campaign.Id, campaign.EngagementId, DateTimeOffset.UtcNow);

        var payloads = env.Host.Services.GetRequiredService<IPayloadStore>();
        var payloadId = Guid.NewGuid();
        await payloads.SaveAsync(new PayloadRecord(
            payloadId, campaign.EngagementId.Value, "Implant", "rust", "application/octet-stream",
            "fingerprint", "live-artifact"u8.ToArray(), 12, DateTimeOffset.UtcNow,
            TokenId: Guid.NewGuid()));
        await campaigns.NoteBuildingAsync(campaign.Id, recipient.Id,
            new DeployTokenId(Guid.NewGuid()), Guid.NewGuid());
        await campaigns.NoteSentAsync(campaign.Id, recipient.Id, payloadId, DateTimeOffset.UtcNow);

        // The engagement's own front serves the recipient's artifact.
        var served = await env.Implant.GetAsync($"/implants/lures/{recipient.LureId:N}");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("live-artifact",
            System.Text.Encoding.UTF8.GetString(await served.Content.ReadAsByteArrayAsync()));

        // The shared operator front carries no lure ingress, and an unknown
        // id is indistinguishable from a revoked one.
        Assert.Equal(HttpStatusCode.NotFound,
            (await env.Http.GetAsync($"/implants/lures/{recipient.LureId:N}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await env.Implant.GetAsync($"/implants/lures/{Guid.NewGuid():N}")).StatusCode);
    }

    private static async Task<string> CreateEngagementAsync(HttpClient http)
    {
        var created = await http.PostAsJsonAsync("/engagements", new { name = "lure-live" });
        created.EnsureSuccessStatusCode();
        var engagement = await created.Content.ReadFromJsonAsync<EngagementBody>();
        return engagement!.EngagementId;
    }

    private sealed record EngagementBody(string EngagementId, string Name);

    // A real-Kestrel host with the shared operator front and a runtime
    // engagement listener -- the BrowserHookTests env shape, pointed at
    // the lure routes.
    private sealed class LureEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;

        /// <summary>The engagement listener's bind socket, where lure ingress lives.</summary>
        public HttpClient Implant { get; private set; } = null!;

        public static async Task<LureEnv> StartAsync()
        {
            var env = new LureEnv();

            var operatorBind = $"127.0.0.1:{TestSupport.GetFreeTcpPort()}";
            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder.UseRodListeners(new List<ListenerConfig>
                {
                    new(
                        Name: "operator-http",
                        Transport: "http",
                        BindAddress: operatorBind,
                        PublicEndpoint: "http://localhost:5080"),
                }))
                .Build();
            await env.Host.StartAsync();

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://{operatorBind}"),
            };
            await AuthenticatedHost.LoginAsync(env.Http);
            return env;
        }

        /// <summary>
        /// Creates an engagement-scoped http listener through the API and
        /// points <see cref="Implant"/> at its bind socket.
        /// </summary>
        public async Task CreateEngagementListenerAsync(string engagementId)
        {
            var created = await Http.PostAsJsonAsync(
                $"/engagements/{engagementId}/listeners",
                new ListenerEndpoints.CreateListenerRequest(
                    Name: "lure-front-" + Guid.NewGuid().ToString("N")[..6],
                    Transport: "http",
                    BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
                    PublicEndpoint: "http://lure.example.test"));
            created.EnsureSuccessStatusCode();
            var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
            Assert.NotNull(listener);
            Implant?.Dispose();
            Implant = new HttpClient { BaseAddress = new Uri($"http://{listener!.BindAddress}") };
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            Implant?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }
}
