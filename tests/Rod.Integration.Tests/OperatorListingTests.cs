using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Operators;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap M1.5 acceptance: the operator UI's read views are served over the
/// same HTTP API the skeleton already exposes. <c>GET /engagements</c> lists
/// engagements (with the owner handle), and <c>GET /engagements/{id}/implants</c>
/// lists an engagement's enrolled sessions with an online indicator. Drives the
/// in-memory TestServer end to end: create -> enroll -> list. Every route is
/// operator-facing and requires the cookie session; the owner of any created
/// engagement is the logged-in operator.
/// </summary>
public class OperatorListingTests
{
    private static (HttpClient Client, IHost Host, OperatorId OperatorId) CreateClient()
        => AuthenticatedHost.Create();

    private static async Task<string> CreateEngagementAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: name));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(created);
        return created!.EngagementId;
    }

    private static async Task<string> MintTokenAsync(HttpClient client, string engagementId)
    {
        var response = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
        Assert.NotNull(token);
        return token!.Secret;
    }

    [Fact]
    public async Task GetEngagements_ListsCreatedEngagements_WithOwnerHandle()
    {
        var (client, host, _) = CreateClient();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var id = await CreateEngagementAsync(client, "Operation Smokeshow");

            var response = await client.GetAsync("/engagements");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse[]>();
            Assert.NotNull(body);
            var match = Assert.Single(body!, e => e.EngagementId == id);
            Assert.Equal("Operation Smokeshow", match.Name);
            // The owner handle is the logged-in operator's, joined from the
            // operator record rather than named in the request.
            Assert.Equal(AuthenticatedHost.Handle, match.OwnerHandle);
        }
    }

    [Fact]
    public async Task GetEngagements_ReturnsEmptyArray_WhenNoneExist()
    {
        var (client, host, _) = CreateClient();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);

            var response = await client.GetAsync("/engagements");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse[]>();
            Assert.NotNull(body);
            Assert.Empty(body!);
        }
    }

    [Fact]
    public async Task GetImplants_ListsEnrolledImplants_ForEngagement()
    {
        var (client, host, _) = CreateClient();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client, "Operation Lantern");
            var secret = await MintTokenAsync(client, engagementId);

            var enrollResponse = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null));
            enrollResponse.EnsureSuccessStatusCode();
            var enrolled = await enrollResponse.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.NotNull(enrolled);

            var response = await client.GetAsync($"/engagements/{engagementId}/implants");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ImplantEndpoints.ImplantResponse[]>();
            Assert.NotNull(body);
            var match = Assert.Single(body!, i => i.ImplantId == enrolled!.ImplantId);
            Assert.Equal(engagementId, match.EngagementId);
            // No beacon stream in this test, so the enrolled implant is offline.
            Assert.False(match.IsOnline);
            Assert.False(string.IsNullOrWhiteSpace(match.Class));
        }
    }

    [Fact]
    public async Task GetImplants_Returns400_ForMalformedEngagementId()
    {
        var (client, host, _) = CreateClient();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);

            var response = await client.GetAsync("/engagements/not-a-guid/implants");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task GetImplantTasks_ListsIssuedTasks_ForImplant()
    {
        var (client, host, _) = CreateClient();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client, "Operation Beacon");
            var secret = await MintTokenAsync(client, engagementId);

            var enrollResponse = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null));
            enrollResponse.EnsureSuccessStatusCode();
            var enrolled = await enrollResponse.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.NotNull(enrolled);
            Assert.False(string.IsNullOrWhiteSpace(enrolled!.ImplantId));
            var implantId = enrolled.ImplantId!;

            await client.PostAsJsonAsync($"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(
                    ImplantId: implantId,
                    Verb: "shell.exec",
                    Arguments: "whoami"));

            var response = await client.GetAsync(
                $"/engagements/{engagementId}/implants/{implantId}/tasks");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ImplantEndpoints.ImplantTaskResponse[]>();
            Assert.NotNull(body);
            var match = Assert.Single(body!);
            Assert.Equal("shell.exec", match.Verb);
            Assert.Equal("whoami", match.Arguments);
            Assert.Equal("Queued", match.Status);
        }
    }
}
