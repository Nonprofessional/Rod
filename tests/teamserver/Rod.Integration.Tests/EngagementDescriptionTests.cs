using System.Net.Http.Json;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: the engagement's free-text description -- the working record a
/// new engagement starts from -- round-trips through the create response and
/// the engagement list, with the blank-is-absent rule the create form relies
/// on (an empty description field stores null, not whitespace).
/// </summary>
public class EngagementDescriptionTests
{
    [Fact]
    public async Task Create_WithDescription_RoundTripsThroughTheList()
    {
        var (client, _, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var createResponse = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(
                Name: "Operation Record",
                Description: "  internal record: scope and contacts live with the crew  "));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content
            .ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(created);
        Assert.Equal("internal record: scope and contacts live with the crew", created!.Description);

        var list = await client.GetFromJsonAsync<EngagementEndpoints.EngagementResponse[]>("/engagements");
        Assert.NotNull(list);
        Assert.Contains(list, e => e.EngagementId == created.EngagementId && e.Description == created.Description);
    }

    [Fact]
    public async Task Create_WithoutDescription_IsNullNotWhitespace()
    {
        var (client, _, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var omitted = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Bare"));
        omitted.EnsureSuccessStatusCode();
        var bare = await omitted.Content
            .ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(bare);
        Assert.Null(bare!.Description);

        var blank = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Blank", Description: "   "));
        blank.EnsureSuccessStatusCode();
        var hollow = await blank.Content
            .ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(hollow);
        Assert.Null(hollow!.Description);
    }
}
