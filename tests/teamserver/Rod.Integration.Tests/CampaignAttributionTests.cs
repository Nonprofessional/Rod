using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Campaigns;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Deployment;

namespace Rod.Integration.Tests;

/// <summary>
/// The enrollment-attribution half of the delivery campaign (architecture.md
/// Sec 11.5): a credential baked for a campaign recipient stamps the
/// enrollment with campaign and recipient -- on the implant row, in the
/// ImplantEnrolled audit fact, and as the recipient's executed binding --
/// while an ordinary enrollment proceeds with no attribution at all. The
/// shared enroll flow drives this (web, stream, and DNS carriages all ride
/// it), so the web route stands in for every carriage.
/// </summary>
public class CampaignAttributionTests
{
    private sealed record Harness(HttpClient Client, IHost Host, EngagementId Engagement, ICampaignStore Campaigns);

    private static async Task<Harness> NewAsync()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var created = await client.PostAsJsonAsync("/engagements", new { name = "attribution" });
        created.EnsureSuccessStatusCode();
        var engagement = await created.Content.ReadFromJsonAsync<EngagementBody>();
        Assert.True(EngagementId.TryParse(engagement!.EngagementId, out var engagementId));

        var campaigns = host.Services.GetRequiredService<ICampaignStore>();
        return new Harness(client, host, engagementId, campaigns);
    }

    private static async Task<(string Secret, DeployTokenId TokenId)> MintTokenAsync(HttpClient client, EngagementId engagement)
    {
        var minted = await client.PostAsJsonAsync(
            $"/engagements/{engagement}/deploy-tokens", new { });
        minted.EnsureSuccessStatusCode();
        var token = await minted.Content.ReadFromJsonAsync<TokenBody>();
        var tokenId = new DeployTokenId(Guid.Parse(token!.DeployTokenId));
        return (token.Secret, tokenId);
    }

    private static async Task<Campaign> CampaignWithRecipientAsync(
        ICampaignStore campaigns, EngagementId engagement, IHost host, string email)
    {
        var recipient = new CampaignRecipient(CampaignRecipientId.New(), email, null, Guid.NewGuid());
        var campaign = new Campaign(
            CampaignId.New(), engagement, "attribution-run", AuthenticatedHost.GetOperatorId(host),
            DateTimeOffset.UtcNow,
            "relay.example", 587, CampaignRelayTls.StartTls, null, null, "sender@example.com",
            "Subject", "Open {{link}}", false, "{}", Guid.NewGuid(), [recipient]);
        await campaigns.SaveAsync(campaign);
        await campaigns.LaunchAsync(campaign.Id, engagement, DateTimeOffset.UtcNow);
        return campaign;
    }

    [Fact]
    public async Task Enroll_WithCampaignBoundToken_StampsAttributionEverywhere()
    {
        var h = await NewAsync();

        // The per-recipient credential: minted the way the send engine will
        // mint it, and bound on the recipient row before the artifact runs.
        var (secret, tokenId) = await MintTokenAsync(h.Client, h.Engagement);

        var campaign = await CampaignWithRecipientAsync(h.Campaigns, h.Engagement, h.Host, "target@example.com");
        var recipient = campaign.Recipients[0];
        Assert.True(await h.Campaigns.NoteBuildingAsync(campaign.Id, recipient.Id, tokenId, Guid.NewGuid()));

        // The lure was followed: the artifact runs and enrolls with the
        // baked credential.
        var implantId = await EngagementSetup.EnrollAsync(h.Client, secret);

        // The implant row carries the campaign and the recipient.
        var implants = h.Host.Services.GetRequiredService<IImplantRepository>();
        var implant = await implants.FindAsync(new ImplantId(Guid.Parse(implantId)));
        Assert.NotNull(implant);
        Assert.Equal(campaign.Id, implant!.CampaignId);
        Assert.Equal(recipient.Id, implant.CampaignRecipientId);

        // The audit fact names the campaign and the recipient -- the words
        // the acceptance criterion reads.
        var audit = h.Host.Services.GetRequiredService<IAuditStore>();
        var facts = await audit.ListAsync(h.Engagement.Value);
        var enrollFact = facts.Single(f => f.Kind == AuditEventKind.ImplantEnrolled);
        Assert.Contains("campaign='attribution-run'", enrollFact.Payload);
        Assert.Contains("recipient=target@example.com", enrollFact.Payload);

        // The recipient row got its executed binding: the implant that
        // followed the lure, stamped once.
        var readBack = (await h.Campaigns.FindAsync(campaign.Id))!.Recipients[0];
        Assert.NotNull(readBack.ExecutedAt);
        Assert.Equal(implantId, readBack.EnrolledImplantId?.ToString("N"));
    }

    [Fact]
    public async Task Enroll_WithOrdinaryToken_CarriesNoAttribution()
    {
        var h = await NewAsync();

        var (secret, _) = await MintTokenAsync(h.Client, h.Engagement);

        // No campaign anywhere near this engagement's tokens.
        var implantId = await EngagementSetup.EnrollAsync(h.Client, secret);

        var implants = h.Host.Services.GetRequiredService<IImplantRepository>();
        var implant = await implants.FindAsync(new ImplantId(Guid.Parse(implantId)));
        Assert.Null(implant!.CampaignId);
        Assert.Null(implant.CampaignRecipientId);

        var audit = h.Host.Services.GetRequiredService<IAuditStore>();
        var facts = await audit.ListAsync(h.Engagement.Value);
        var enrollFact = facts.Single(f => f.Kind == AuditEventKind.ImplantEnrolled);
        Assert.DoesNotContain("campaign=", enrollFact.Payload);
    }

    private sealed record EngagementBody(string EngagementId, string Name);
    private sealed record TokenBody(string DeployTokenId, string Secret);
}
