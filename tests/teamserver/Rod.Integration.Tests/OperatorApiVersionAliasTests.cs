using System.Net;
using System.Net.Http.Json;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the versioned operator-API alias (architecture.md Sec 15.3,
/// ADR-006): <c>/api/v1/&lt;path&gt;</c> resolves against the same operator
/// endpoints the unversioned routes serve, with the same authentication and
/// engagement-scope behavior -- the versioned space programmatic consumers
/// address is the operator API, not a second implementation of it.
/// </summary>
public class OperatorApiVersionAliasTests
{
    [Theory]
    [InlineData("/engagements")]
    [InlineData("/api/v1/engagements")]
    public async Task TheOperatorSurface_AnswerAtBothAddresses(string path)
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using var _client = client;
        using var _host = host;
        await AuthenticatedHost.LoginAsync(client);

        var createResponse = await client.PostAsJsonAsync("/api/v1/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Versioned"));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content
            .ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(created);

        // The created engagement is readable through both addresses.
        var list = await client.GetFromJsonAsync<EngagementEndpoints.EngagementResponse[]>(path);
        Assert.NotNull(list);
        Assert.Contains(list, e => e.EngagementId == created!.EngagementId);
    }

    [Fact]
    public async Task TheVersionedSurface_KeepsTheAuthenticationGate()
    {
        var (client, _, _) = AuthenticatedHost.Create();

        var response = await client.GetAsync("/api/v1/engagements");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheAlias_StripsExactlyTheVersionPrefix()
    {
        var (client, _, _) = AuthenticatedHost.Create();

        // /api/v1 exactly (no trailing path) is not a route; the alias must
        // leave it alone rather than collapse it onto the SPA root.
        var bare = await client.GetAsync("/api/v1");
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);

        // A doubled prefix strips once: /api/v1/api/v1/engagements is the
        // unversioned /api/v1/engagements, which does not exist.
        var doubled = await client.GetAsync("/api/v1/api/v1/engagements");
        Assert.Equal(HttpStatusCode.NotFound, doubled.StatusCode);
    }

    [Fact]
    public async Task TheHealthProbe_AnswersThroughTheAlias()
    {
        var (client, _, _) = AuthenticatedHost.Create();

        var response = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
