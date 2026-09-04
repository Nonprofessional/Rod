using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Rod.Audit;
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

    [Fact]
    public async Task ABuild_MintsAndReportsItsBakedToken_WhichRevokes()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            // The build mints its own enrollment credential and bakes it in;
            // the response reports the token's id -- enough to revoke, never
            // enough to reuse.
            var built = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/payloads",
                new PayloadEndpoints.BuildPayloadRequest(
                    Language: "DotNet", Class: "Stage2", TargetOs: "linux", TargetArch: "amd64",
                    Endpoint: "https://c2.example.test", UriPath: "/beacon",
                    SleepSeconds: 30, JitterSeconds: 10, KillDate: null));
            built.EnsureSuccessStatusCode();
            var artifact = await built.Content.ReadFromJsonAsync<PayloadEndpoints.BuildPayloadResponse>();
            Assert.False(string.IsNullOrWhiteSpace(artifact!.TokenId));

            // The mint is on the trail with the baked shape named.
            var audit = host.Services.GetRequiredService<IAuditStore>();
            var trail = await audit.ListAsync(Guid.Parse(engagementId));
            Assert.Contains(trail, e =>
                e.Kind == AuditEventKind.StagerTokenMinted && e.Payload.Contains("bakedIntoPayload"));

            // Revocation is the leak answer for a baked credential: the id
            // stops working, the second attempt honestly 404s, and the
            // revocation lands on the trail.
            var revoked = await client.PostAsync(
                $"/engagements/{engagementId}/stager-tokens/{artifact.TokenId}:revoke", null);
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            var revokedAgain = await client.PostAsync(
                $"/engagements/{engagementId}/stager-tokens/{artifact.TokenId}:revoke", null);
            Assert.Equal(HttpStatusCode.NotFound, revokedAgain.StatusCode);

            trail = await audit.ListAsync(Guid.Parse(engagementId));
            Assert.Contains(trail, e =>
                e.Kind == AuditEventKind.StagerTokenRevoked && e.Outcome == artifact.TokenId);
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
