using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// The HTTP slice of roadmap M5.2 (architecture.md Sec 5.2): a child implant
/// enrols from a parent within scope, with the parentage linkage recorded and
/// surfaced. Complements <see cref="ChildEnrollmentServiceTests"/> (the core
/// parent-resolution rules) by driving the full enroll endpoint + the operator
/// listing through the in-memory TestServer. The accept point is that a child
/// enrolled over HTTP carries its parent, the listing shows the lineage, and a
/// foreign-engagement parent is refused with no signal beyond the existing
/// 401/BadToken shape.
/// </summary>
public class ChildEnrollmentHttpTests
{
    [Fact]
    public async Task ChildEnrols_OverHttp_RecordsParent_AndListingSurfacesIt()
    {
        // The M5.2 accept point on the wire: a child enrolls with a parent id
        // over HTTP, the enroll response echoes the parent, and the operator
        // listing shows the parentage so the UI can render lineage.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var (engagementId, secret) = await MintTokenForNewEngagementAsync(client);

            // A parent enrolls first (top-level, no parent of its own).
            var parentResponse = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null));
            Assert.Equal(HttpStatusCode.OK, parentResponse.StatusCode);
            var parent = await parentResponse.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.NotNull(parent);
            Assert.Null(parent!.ParentImplantId); // top-level implant has no parent

            // Mint a fresh token for the same engagement so the child can enroll
            // (stager tokens are single-use).
            var childSecret = await MintStagerTokenAsync(client, engagementId);

            // The child enrolls carrying the parent id.
            var childResponse = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(
                    StagerTokenSecret: childSecret,
                    Class: null,
                    PublicKey: null,
                    ParentImplantId: parent.ImplantId));
            Assert.Equal(HttpStatusCode.OK, childResponse.StatusCode);
            var child = await childResponse.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.NotNull(child);
            Assert.Equal(EnrollStatus.Ok, child!.Status);
            Assert.Equal(parent.ImplantId, child.ParentImplantId);
            Assert.Equal(engagementId, child.EngagementId);

            // The operator listing surfaces both implants and the parentage.
            var listed = await client.GetFromJsonAsync<ImplantEndpoints.ImplantResponse[]>(
                $"/engagements/{engagementId}/implants");
            Assert.NotNull(listed);
            var childRow = Assert.Single(listed!, i => i.ImplantId == child.ImplantId);
            Assert.Equal(parent.ImplantId, childRow.ParentImplantId);
            var parentRow = Assert.Single(listed!, i => i.ImplantId == parent.ImplantId);
            Assert.Null(parentRow.ParentImplantId);
        }
    }

    [Fact]
    public async Task ChildEnrol_ReturnsBadToken_ForForeignEngagementParent()
    {
        // A parent in another engagement is refused (architecture.md Sec 3, 5.2):
        // the child cannot be grafted across the engagement boundary. The refusal
        // collapses to the existing 401/BadToken shape so the wire gives no
        // signal distinguishing "bad parent" from "bad token".
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            // Two independent engagements with their own tokens and parents.
            var (_, parentSecret) = await MintTokenForNewEngagementAsync(client);
            var parentResponse = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: parentSecret, Class: null));
            var parent = await parentResponse.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();

            var (otherEngagement, childSecret) = await MintTokenForNewEngagementAsync(client);

            // The child tries to enroll into the other engagement naming the
            // foreign parent.
            var response = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(
                    StagerTokenSecret: childSecret,
                    Class: null,
                    PublicKey: null,
                    ParentImplantId: parent!.ImplantId));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.Equal(EnrollStatus.BadToken, body!.Status);
            Assert.Null(body.ImplantId);

            // Nothing was enrolled into the other engagement.
            var listed = await client.GetFromJsonAsync<ImplantEndpoints.ImplantResponse[]>(
                $"/engagements/{otherEngagement}/implants");
            Assert.NotNull(listed);
            Assert.Empty(listed!);
        }
    }

    [Fact]
    public async Task ChildEnrol_ReturnsBadRequest_ForMalformedParentId()
    {
        // A parent id that is not a valid identifier is a malformed request, not
        // a token failure: the token stays intact for a retry.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var (_, secret) = await MintTokenForNewEngagementAsync(client);

            var response = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(
                    StagerTokenSecret: secret,
                    Class: null,
                    PublicKey: null,
                    ParentImplantId: "not-a-guid"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

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

    // Creates an engagement and mints its first stager token in one shot. Returns
    // (engagementId, secret) so a test can derive further tokens for siblings.
    private static async Task<(string EngagementId, string Secret)> MintTokenForNewEngagementAsync(HttpClient client)
    {
        var createResponse = await client.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
            OwnerId: Guid.NewGuid(),
            OwnerHandle: "cneale",
            OwnerDisplayName: "Cecil Neale",
            Name: "Operation Smokeshow"));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(created);
        var secret = await MintStagerTokenAsync(client, created!.EngagementId);
        return (created.EngagementId, secret);
    }

    private static async Task<string> MintStagerTokenAsync(HttpClient client, string engagementId)
    {
        var response = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
        return token!.Secret;
    }
}
