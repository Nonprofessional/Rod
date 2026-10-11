using Rod.CoreState.Campaigns;
using Rod.CoreState.Deployment;
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// The delivery-campaign aggregate and its store (architecture.md
/// Sec 11.5): the state invariants (launch once, revoke freezes, completion
/// only when every recipient is terminal), the recipient arc guards
/// (single-attempt delivery, monotonic evidence stamps), and the template
/// grammar the create path validates against.
/// </summary>
public class CampaignTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Campaign NewCampaign(params (string Email, string? Name)[] recipients)
    {
        var rows = recipients.Select(r => new CampaignRecipient(
            CampaignRecipientId.New(), r.Email, r.Name, Guid.NewGuid())).ToArray();
        return new Campaign(
            CampaignId.New(), EngagementId.New(), "test", OperatorId.New(), Now,
            "relay.example", 587, CampaignRelayTls.StartTls, null, null,
            "sender@example.com",
            "Quarterly renewal", "Please review {{link}}", false,
            "{}", Guid.NewGuid(), rows);
    }

    // --- The campaign state machine. ---

    [Fact]
    public async Task Launch_MovesDraftToLaunched_Once()
    {
        var store = new InMemoryCampaignStore();
        var campaign = NewCampaign(("a@example.com", null));
        await store.SaveAsync(campaign);

        Assert.True(await store.LaunchAsync(campaign.Id, campaign.EngagementId, Now));
        var launched = await store.FindAsync(campaign.Id);
        Assert.Equal(CampaignState.Launched, launched!.State);
        Assert.Equal(Now, launched.LaunchedAt);

        // A second launch answers false -- the 409 shape, not a second arc.
        Assert.False(await store.LaunchAsync(campaign.Id, campaign.EngagementId, Now.AddMinutes(1)));
        // A launch scoped to the wrong engagement never finds the row.
        Assert.False(await store.LaunchAsync(campaign.Id, EngagementId.New(), Now));
    }

    [Fact]
    public async Task Revoke_FreezesFromAnyLiveState_AndSticks()
    {
        var store = new InMemoryCampaignStore();
        var campaign = NewCampaign(("a@example.com", null));
        await store.SaveAsync(campaign);

        Assert.True(await store.RevokeAsync(campaign.Id, campaign.EngagementId, Now));
        Assert.False(await store.RevokeAsync(campaign.Id, campaign.EngagementId, Now.AddSeconds(1)));
        Assert.Equal(CampaignState.Revoked, (await store.FindAsync(campaign.Id))!.State);

        // A revoked campaign can neither launch nor complete.
        Assert.False(await store.LaunchAsync(campaign.Id, campaign.EngagementId, Now));
        Assert.False(await store.TryCompleteAsync(campaign.Id, Now));
    }

    [Fact]
    public async Task TryComplete_RequiresLaunchedAndTerminalRecipients()
    {
        var store = new InMemoryCampaignStore();
        var campaign = NewCampaign(("a@example.com", null), ("b@example.com", null));
        await store.SaveAsync(campaign);

        // Not launched: no completion.
        Assert.False(await store.TryCompleteAsync(campaign.Id, Now));

        await store.LaunchAsync(campaign.Id, campaign.EngagementId, Now);
        // A recipient still pending keeps the campaign live.
        Assert.False(await store.TryCompleteAsync(campaign.Id, Now));

        var first = campaign.Recipients[0];
        var second = campaign.Recipients[1];
        var token = DeployTokenId.New();
        await store.NoteBuildingAsync(campaign.Id, first.Id, token, Guid.NewGuid());
        // One terminal, one building: still live.
        await store.NoteFailedAsync(campaign.Id, first.Id, "send: relay refused", Now);
        Assert.False(await store.TryCompleteAsync(campaign.Id, Now));

        await store.NoteBuildingAsync(campaign.Id, second.Id, DeployTokenId.New(), Guid.NewGuid());
        await store.NoteSentAsync(campaign.Id, second.Id, Guid.NewGuid(), Now);
        Assert.True(await store.TryCompleteAsync(campaign.Id, Now));
        Assert.Equal(CampaignState.Completed, (await store.FindAsync(campaign.Id))!.State);
    }

    // --- The recipient arc. ---

    [Fact]
    public async Task RecipientArc_BuildingThenSent_CarriesTokenAndArtifact()
    {
        var store = new InMemoryCampaignStore();
        var campaign = NewCampaign(("a@example.com", "A"));
        await store.SaveAsync(campaign);
        var recipient = campaign.Recipients[0];

        var token = DeployTokenId.New();
        var job = Guid.NewGuid();
        Assert.True(await store.NoteBuildingAsync(campaign.Id, recipient.Id, token, job));

        var read = (await store.FindAsync(campaign.Id))!.Recipients[0];
        Assert.Equal(CampaignRecipientStatus.Building, read.Status);
        Assert.Equal(token, read.EnrollTokenId);
        Assert.Equal(job, read.JobId);

        var payload = Guid.NewGuid();
        Assert.True(await store.NoteSentAsync(campaign.Id, recipient.Id, payload, Now));
        read = (await store.FindAsync(campaign.Id))!.Recipients[0];
        Assert.Equal(CampaignRecipientStatus.Sent, read.Status);
        Assert.Equal(payload, read.PayloadId);
        Assert.Equal(Now, read.SentAt);

        // Delivery is single-attempt: a terminal recipient never moves again.
        Assert.False(await store.NoteSentAsync(campaign.Id, recipient.Id, payload, Now.AddMinutes(1)));
        Assert.False(await store.NoteFailedAsync(campaign.Id, recipient.Id, "again", Now));
        Assert.False(await store.NoteBuildingAsync(campaign.Id, recipient.Id, DeployTokenId.New(), Guid.NewGuid()));
    }

    [Fact]
    public async Task Building_MayResubmitWithAFreshCredential()
    {
        var store = new InMemoryCampaignStore();
        var campaign = NewCampaign(("a@example.com", null));
        await store.SaveAsync(campaign);
        var recipient = campaign.Recipients[0];

        await store.NoteBuildingAsync(campaign.Id, recipient.Id, DeployTokenId.New(), Guid.NewGuid());
        // A lost job's re-submission: the fresh token overwrites the stale binding.
        var fresh = DeployTokenId.New();
        Assert.True(await store.NoteBuildingAsync(campaign.Id, recipient.Id, fresh, Guid.NewGuid()));
        Assert.Equal(fresh, (await store.FindAsync(campaign.Id))!.Recipients[0].EnrollTokenId);
    }

    [Fact]
    public async Task Failure_FromBuildingOnly_WhenBuildRan_FromPendingTooWhenItDidNot()
    {
        var store = new InMemoryCampaignStore();
        var campaign = NewCampaign(("a@example.com", null), ("b@example.com", null));
        await store.SaveAsync(campaign);

        var pending = campaign.Recipients[0];
        var building = campaign.Recipients[1];
        await store.NoteBuildingAsync(campaign.Id, building.Id, DeployTokenId.New(), Guid.NewGuid());

        // A pending recipient can fail (the build profile refused at launch).
        Assert.True(await store.NoteFailedAsync(campaign.Id, pending.Id, "build: refused", Now));
        // A building recipient can fail (the build or the send failed).
        Assert.True(await store.NoteFailedAsync(campaign.Id, building.Id, "send: relay refused", Now));
        Assert.Equal("build: refused", (await store.FindAsync(campaign.Id))!.Recipients[0].Failure);
    }

    // --- Evidence stamps. ---

    [Fact]
    public async Task EvidenceStamps_AreMonotonicFirstStamps()
    {
        var store = new InMemoryCampaignStore();
        var campaign = NewCampaign(("a@example.com", null));
        await store.SaveAsync(campaign);
        var recipient = campaign.Recipients[0];

        Assert.True(await store.NoteOpenedAsync(campaign.Id, recipient.Id, Now));
        Assert.False(await store.NoteOpenedAsync(campaign.Id, recipient.Id, Now.AddMinutes(5)));
        Assert.True(await store.NoteClickedAsync(campaign.Id, recipient.Id, Now.AddMinutes(6)));
        Assert.False(await store.NoteClickedAsync(campaign.Id, recipient.Id, Now.AddMinutes(7)));

        // The executed binding carries the implant id and stamps once.
        var implant = Guid.NewGuid();
        Assert.True(await store.NoteExecutedAsync(campaign.Id, recipient.Id, implant, Now.AddMinutes(8)));
        Assert.False(await store.NoteExecutedAsync(campaign.Id, recipient.Id, Guid.NewGuid(), Now.AddMinutes(9)));

        var read = (await store.FindAsync(campaign.Id))!.Recipients[0];
        Assert.Equal(Now, read.OpenedAt);
        Assert.Equal(Now.AddMinutes(6), read.ClickedAt);
        Assert.Equal(implant, read.EnrolledImplantId);
        Assert.Equal(Now.AddMinutes(8), read.ExecutedAt);
    }

    // --- The public-edge lookups. ---

    [Fact]
    public async Task Lookups_ResolveByLureAndByEnrollToken()
    {
        var store = new InMemoryCampaignStore();
        var campaign = NewCampaign(("a@example.com", null), ("b@example.com", null));
        await store.SaveAsync(campaign);

        var lure = await store.FindByLureAsync(campaign.Recipients[0].LureId);
        Assert.NotNull(lure);
        Assert.Equal(campaign.Id, lure.CampaignId);
        Assert.Equal(campaign.Recipients[0].Id, lure.RecipientId);
        Assert.Equal("a@example.com", lure.RecipientEmail);
        Assert.Null(lure.PayloadId);

        // An unknown lure reads as nothing at all.
        Assert.Null(await store.FindByLureAsync(Guid.NewGuid()));

        // The attribution lookup answers null before the mint (an ordinary
        // build's token never rode a campaign) and the binding after it.
        var token = DeployTokenId.New();
        Assert.Null(await store.FindByEnrollTokenAsync(token));
        await store.NoteBuildingAsync(campaign.Id, campaign.Recipients[1].Id, token, Guid.NewGuid());

        var attribution = await store.FindByEnrollTokenAsync(token);
        Assert.NotNull(attribution);
        Assert.Equal(campaign.Id, attribution.CampaignId);
        Assert.Equal(campaign.Recipients[1].Id, attribution.RecipientId);
        Assert.Equal("b@example.com", attribution.RecipientEmail);
        Assert.Equal("test", attribution.CampaignName);
    }

    [Fact]
    public async Task Listings_ScopeByEngagementAndState()
    {
        var store = new InMemoryCampaignStore();
        var first = NewCampaign(("a@example.com", null));
        var second = NewCampaign(("b@example.com", null));
        await store.SaveAsync(first);
        await store.SaveAsync(second);
        await store.LaunchAsync(second.Id, second.EngagementId, Now);

        var scoped = await store.ListByEngagementAsync(first.EngagementId);
        Assert.Single(scoped);
        Assert.Equal(first.Id, scoped[0].Id);

        var launched = await store.ListLaunchedAsync();
        Assert.Single(launched);
        Assert.Equal(second.Id, launched[0].Id);

        // The scoped find refuses a foreign engagement's id.
        Assert.Null(await store.FindAsync(second.Id, first.EngagementId));
    }

    // --- The template grammar. ---

    [Theory]
    [InlineData("", "body {{link}}")]
    [InlineData("subject", "")]
    [InlineData("subject", "no link here")]
    [InlineData("subject", "{{attachment}}")]
    public void TemplateValidation_RefusesTheBrokenShapes(string subject, string body)
    {
        Assert.NotNull(CampaignLimits.ValidateTemplate(subject, body));
    }

    [Fact]
    public void TemplateValidation_AcceptsTheKnownFields()
    {
        Assert.Null(CampaignLimits.ValidateTemplate(
            "Hi {{name}}", "Review {{link}} or write {{email}}; pixel {{pixel}}"));
    }

    [Fact]
    public void TemplateRender_MergesPerRecipient()
    {
        var rendered = CampaignLimits.Render(
            "Hi {{ name }} -- {{email}}",
            "https://front.example/implants/lures/abc",
            "https://front.example/implants/lures/abc/open",
            "a@example.com",
            "A. Target");
        Assert.Equal("Hi A. Target -- a@example.com", rendered);
    }

    [Fact]
    public void TemplateRender_NameRendersEmptyWhenAbsent()
    {
        var rendered = CampaignLimits.Render("Hi {{name}},", "l", "p", "a@example.com", null);
        Assert.Equal("Hi ,", rendered);
    }

    [Fact]
    public void PixelInjection_LandsBeforeTheClosingBodyTag_Once()
    {
        var pixel = "https://front.example/pixel";
        var placed = CampaignLimits.AppendPixel("<html><body>hello</body></html>", pixel);
        Assert.Equal($"<html><body>hello<img src=\"{pixel}\" width=\"1\" height=\"1\" alt=\"\"></body></html>", placed);

        var unclosed = CampaignLimits.AppendPixel("<p>hello", pixel);
        Assert.Equal($"<p>hello<img src=\"{pixel}\" width=\"1\" height=\"1\" alt=\"\">", unclosed);
    }
}
