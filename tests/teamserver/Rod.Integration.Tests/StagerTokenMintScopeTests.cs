using System.Net;
using System.Net.Http.Json;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// The mint scope on the wire: the optional JSON body sizes a batch token and
/// the response carries the scope back, a body-less post keeps the single-use
/// default, and out-of-range values are refused loudly rather than clamped.
/// </summary>
public class StagerTokenMintScopeTests
{
    [Fact]
    public async Task Batch_Scope_Rides_The_Optional_Body()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var minted = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/stager-tokens",
                new EngagementEndpoints.MintStagerTokenRequest(MaxUses: 3, LifetimeSeconds: 8 * 3600));
            Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
            var token = await minted.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
            Assert.Equal(3, token!.MaxUses);
            Assert.Equal(token.IssuedAt.AddHours(8), token.ExpiresAt);

            var plain = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", null);
            Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
            var single = await plain.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
            Assert.Equal(1, single!.MaxUses);
        }
    }

    [Fact]
    public async Task Out_Of_Range_Scope_Is_Refused()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var noUses = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/stager-tokens",
                new EngagementEndpoints.MintStagerTokenRequest(MaxUses: 0, LifetimeSeconds: null));
            Assert.Equal(HttpStatusCode.BadRequest, noUses.StatusCode);

            var noWindow = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/stager-tokens",
                new EngagementEndpoints.MintStagerTokenRequest(MaxUses: null, LifetimeSeconds: 1));
            Assert.Equal(HttpStatusCode.BadRequest, noWindow.StatusCode);
        }
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/engagements", new EngagementEndpoints.CreateEngagementRequest(Name: "mint scope"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }
}
