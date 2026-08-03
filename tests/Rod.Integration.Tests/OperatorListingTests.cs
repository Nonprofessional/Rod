using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap M1.5 acceptance: the operator UI's read views are served over the
/// same HTTP API the skeleton already exposes. <c>GET /engagements</c> lists
/// engagements (with the owner handle), and <c>GET /engagements/{id}/implants</c>
/// lists an engagement's enrolled sessions with an online indicator. Drives the
/// in-memory TestServer end to end: create -> enroll -> list.
/// </summary>
public class OperatorListingTests
{
    private static (HttpClient Client, IHost Host) CreateClient()
    {
        IHost host = TransportHost.CreateHostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder.UseTestServer())
            .Build();
        host.Start();

        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        var client = server.CreateClient();
        client.BaseAddress = new Uri("http://localhost");
        return (client, host);
    }

    private static async Task<(string EngagementId, string OwnerHandle)> CreateEngagementAsync(
        HttpClient client, string name, string ownerHandle)
    {
        var response = await client.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
            OwnerId: Guid.NewGuid(),
            OwnerHandle: ownerHandle,
            OwnerDisplayName: ownerHandle,
            Name: name));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(created);
        return (created!.EngagementId, created.OwnerHandle);
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
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var (id, handle) = await CreateEngagementAsync(client, "Operation Smokeshow", "cneale");

            var response = await client.GetAsync("/engagements");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse[]>();
            Assert.NotNull(body);
            var match = Assert.Single(body!, e => e.EngagementId == id);
            Assert.Equal("Operation Smokeshow", match.Name);
            // The owner handle is joined from the operator, not the engagement.
            Assert.Equal(handle, match.OwnerHandle);
        }
    }

    [Fact]
    public async Task GetEngagements_ReturnsEmptyArray_WhenNoneExist()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
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
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var (engagementId, _) = await CreateEngagementAsync(client, "Operation Lantern", "jdoe");
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
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var response = await client.GetAsync("/engagements/not-a-guid/implants");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task GetImplantTasks_ListsIssuedTasks_ForImplant()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var (engagementId, _) = await CreateEngagementAsync(client, "Operation Beacon", "mholloway");
            var secret = await MintTokenAsync(client, engagementId);

            var enrollResponse = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null));
            enrollResponse.EnsureSuccessStatusCode();
            var enrolled = await enrollResponse.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.NotNull(enrolled);
            Assert.False(string.IsNullOrWhiteSpace(enrolled!.ImplantId));
            var implantId = enrolled.ImplantId!;

            var ownerId = Guid.NewGuid();
            await client.PostAsJsonAsync($"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(
                    ImplantId: implantId,
                    IssuedBy: ownerId,
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
