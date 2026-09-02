using System.Net;
using System.Net.Http.Json;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: the engagement's working record is editable -- the name and
/// free-text description -- through GET one / PUT, with the close-out rules
/// the aggregate enforces: a retired engagement's record is sealed (409), and
/// the edit lands in the audit trail as an <c>EngagementUpdated</c> fact.
/// </summary>
public class EngagementEditTests
{
    [Fact]
    public async Task Edit_NameAndDescription_RoundTripThroughGetAndList()
    {
        var (client, _, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var create = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Draft"));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(created);

        var edit = await client.PutAsJsonAsync($"/engagements/{created!.EngagementId}",
            new EngagementEndpoints.EditEngagementRequest(
                Name: "Operation Final",
                Description: "scope: the lab segment; owner: red team"));
        edit.EnsureSuccessStatusCode();
        var edited = await edit.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(edited);
        Assert.Equal("Operation Final", edited!.Name);
        Assert.Equal("scope: the lab segment; owner: red team", edited.Description);

        // The single read and the list both carry the edited record; the
        // close-out fields ride along unchanged.
        var one = await client.GetFromJsonAsync<EngagementEndpoints.EngagementResponse>(
            $"/engagements/{created.EngagementId}");
        Assert.NotNull(one);
        Assert.Equal("Operation Final", one!.Name);
        Assert.Null(one.FrozenAt);

        var list = await client.GetFromJsonAsync<EngagementEndpoints.EngagementResponse[]>("/engagements");
        Assert.NotNull(list);
        Assert.Contains(list, e => e.EngagementId == created.EngagementId && e.Name == "Operation Final");
    }

    [Fact]
    public async Task Edit_BlankDescription_ClearsIt()
    {
        var (client, _, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var create = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Notes", Description: "draft notes"));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(created);
        Assert.NotNull(created!.Description);

        var edit = await client.PutAsJsonAsync($"/engagements/{created.EngagementId}",
            new EngagementEndpoints.EditEngagementRequest(Name: "Operation Notes", Description: null));
        edit.EnsureSuccessStatusCode();
        var edited = await edit.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(edited);
        Assert.Null(edited!.Description);
    }

    [Fact]
    public async Task Edit_BlankName_IsRejected()
    {
        var (client, _, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var create = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Keep"));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(created);

        var edit = await client.PutAsJsonAsync($"/engagements/{created!.EngagementId}",
            new EngagementEndpoints.EditEngagementRequest(Name: "   ", Description: null));
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
    }

    [Fact]
    public async Task Edit_UnknownEngagement_IsNotFound()
    {
        var (client, _, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var edit = await client.PutAsJsonAsync($"/engagements/{Guid.NewGuid()}",
            new EngagementEndpoints.EditEngagementRequest(Name: "Ghost", Description: null));
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
    }

    [Fact]
    public async Task Edit_RetiredEngagement_IsSealed()
    {
        var (client, _, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var create = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Done"));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(created);

        // The close-out arc: freeze, then retire -- terminal, and the record
        // stops moving with it.
        var freeze = await client.PostAsync($"/engagements/{created!.EngagementId}:freeze", content: null);
        freeze.EnsureSuccessStatusCode();
        var retire = await client.PostAsync($"/engagements/{created.EngagementId}:retire", content: null);
        retire.EnsureSuccessStatusCode();

        var edit = await client.PutAsJsonAsync($"/engagements/{created.EngagementId}",
            new EngagementEndpoints.EditEngagementRequest(Name: "Operation Rewritten", Description: null));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
    }

    [Fact]
    public async Task Edit_IsRecordedOnTheAuditTrail()
    {
        var (client, _, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var create = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Trail"));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(created);

        var edit = await client.PutAsJsonAsync($"/engagements/{created!.EngagementId}",
            new EngagementEndpoints.EditEngagementRequest(Name: "Operation Trail", Description: "set"));
        edit.EnsureSuccessStatusCode();

        var trail = await client.GetFromJsonAsync<AuditEndpoints.AuditListResponse>(
            $"/engagements/{created.EngagementId}/audit");
        Assert.NotNull(trail);
        var recorded = Assert.Single(trail!.Items, e => e.Kind == nameof(Rod.Audit.AuditEventKind.EngagementUpdated));
        Assert.Equal("edit-engagement", recorded.Verb);
        Assert.Equal(created.EngagementId, recorded.Outcome);
    }
}
