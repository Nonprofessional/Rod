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
/// Roadmap M1.1 acceptance: create an engagement over HTTP, then mint a stager
/// token for it -- end to end through the in-memory TestServer. This exercises
/// the core-state domain (ports, aggregates, stager-token service) driven by the
/// transport-layer endpoints, proving the vertical slice works as a whole.
/// </summary>
public class EngagementHttpTests
{
    /// <summary>
    /// Builds the teamserver host under the in-memory <see cref="TestServer"/> and
    /// hands back an <see cref="HttpClient"/> rooted at it. The host owns the
    /// client's lifetime and is disposed together with it.
    /// </summary>
    private static (HttpClient Client, IHost Host) CreateClient()
    {
        IHost host = TransportHost.CreateHostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder.UseTestServer())
            .Build();
        host.Start();

        // TestServer is the IServer registered by UseTestServer; it knows how to
        // serve the pipeline in memory.
        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        var client = server.CreateClient();
        client.BaseAddress = new Uri("http://localhost");
        return (client, host);
    }

    [Fact]
    public async Task PostEngagements_CreatesEngagement_WithOwnerAsMember()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var ownerId = Guid.NewGuid();

            var response = await client.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
                OwnerId: ownerId,
                OwnerHandle: "cneale",
                OwnerDisplayName: "Cecil Neale",
                Name: "Operation Smokeshow"));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
            Assert.NotNull(created);
            Assert.Equal("Operation Smokeshow", created!.Name);
            Assert.Equal(ownerId.ToString("N"), created.OwnerId);
            Assert.Equal("cneale", created.OwnerHandle);
            Assert.False(string.IsNullOrWhiteSpace(created.EngagementId));
            Assert.True(Guid.TryParse(created.EngagementId, out _));
        }
    }

    [Fact]
    public async Task PostStagerToken_MintsToken_ForCreatedEngagement()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            // Create the engagement first -- the mint endpoint keys off its id.
            var createResponse = await client.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
                OwnerId: Guid.NewGuid(),
                OwnerHandle: "jdoe",
                OwnerDisplayName: "Jane Doe",
                Name: "Operation Lantern"));
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
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var response = await client.PostAsync($"/engagements/{Guid.NewGuid()}/stager-tokens", content: null);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task PostEngagements_Returns400_WhenNameMissing()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var response = await client.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
                OwnerId: Guid.NewGuid(),
                OwnerHandle: "x",
                OwnerDisplayName: "X",
                Name: ""));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
