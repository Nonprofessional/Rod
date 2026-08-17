using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: create an engagement over HTTP, then mint a stager
/// token for it -- end to end through the in-memory TestServer. This exercises
/// the core-state domain (ports, aggregates, stager-token service) driven by the
/// transport-layer endpoints, proving the vertical slice works as a whole. Every
/// engagement route now requires an authenticated operator session (operator
/// authentication): the owner is the logged-in
/// operator, recorded by the server off the session principal rather than named
/// in the request body.
/// </summary>
public class EngagementHttpTests
{
    [Fact]
    public async Task PostEngagements_Anonymous_IsUnauthorized()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            // No login: the operator session is the gate.
            var response = await client.PostAsJsonAsync("/engagements",
                new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Smokeshow"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task PostEngagements_CreatesEngagement_RecordingOwner()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);

            var response = await client.PostAsJsonAsync("/engagements",
                new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Smokeshow"));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
            Assert.NotNull(created);
            Assert.Equal("Operation Smokeshow", created!.Name);
            // The owner is the authenticated operator, recorded by the server.
            Assert.Equal(operatorId.Value.ToString("N"), created.OwnerId);
            Assert.Equal(AuthenticatedHost.Handle, created.OwnerHandle);
            Assert.False(string.IsNullOrWhiteSpace(created.EngagementId));
            Assert.True(Guid.TryParse(created.EngagementId, out _));
        }
    }

    [Fact]
    public async Task PostStagerToken_MintsToken_ForCreatedEngagement()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);

            // Create the engagement first -- the mint endpoint keys off its id.
            var createResponse = await client.PostAsJsonAsync("/engagements",
                new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Lantern"));
            var created = await createResponse.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
            Assert.NotNull(created);

            var mintResponse = await client.PostAsync($"/engagements/{created!.EngagementId}/stager-tokens", content: null);

            Assert.Equal(HttpStatusCode.OK, mintResponse.StatusCode);

            var token = await mintResponse.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
            Assert.NotNull(token);
            Assert.Equal(created.EngagementId, token!.EngagementId);
            // The secret is the single-use value handed back exactly once: non-empty.
            Assert.False(string.IsNullOrWhiteSpace(token.Secret));
            // Glossary: short-lived, bounded-use. Defaults from the in-memory service.
            Assert.Equal(1, token.MaxUses);
            Assert.True(token.ExpiresAt > token.IssuedAt);
        }
    }

    [Fact]
    public async Task PostStagerToken_Returns404_ForUnknownEngagement()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);

            var response = await client.PostAsync($"/engagements/{Guid.NewGuid()}/stager-tokens", content: null);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task PostEngagements_Returns400_WhenNameMissing()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);

            var response = await client.PostAsJsonAsync("/engagements",
                new EngagementEndpoints.CreateEngagementRequest(Name: ""));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
