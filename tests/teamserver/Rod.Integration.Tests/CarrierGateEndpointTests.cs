using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the issuance-time carrier gate (architecture.md Sec 8,
/// Sec 10.3): an enrollment stamped with its build's baked carrier set -- the
/// enroll path derives it off the payload record the redeemed token resolves
/// -- refuses a channel verb no baked carrier could ever claim, instead of
/// queueing it for a stream the artifact will never dial. The payload records
/// here stand in for the build pipeline's; the enrollment reads them exactly
/// the way it reads a real build's.
/// </summary>
public class CarrierGateEndpointTests
{
    [Fact]
    public async Task ChannelVerb_IsRefusedAtIssuance_ForAnEnvelopeOnlyBuild()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var (secret, tokenId) = await MintStagerTokenAsync(client, engagementId);
            await SavePayloadAsync(host, tokenId, engagementId,
                endpoint: "https://front.example.com:8443", beaconEndpoint: null);
            var implantId = await EnrollAsync(client, secret);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.interact", Arguments = "" });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, issued.StatusCode);
            var problem = await issued.Content.ReadFromJsonAsync<EngagementEndpoints.Problem>();
            Assert.NotNull(problem);
            Assert.Contains("carrier", problem!.Error, StringComparison.OrdinalIgnoreCase);

            // One-shot tasking is untouched: the gate is the channel verbs' alone.
            var exec = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.exec", Arguments = "whoami" });
            exec.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task ChannelVerb_IsIssued_ForASplitSocketBuild()
    {
        // The split-socket bake: the beacon entry dials the stream, so the
        // channel verb is claimable even though the enroll front envelopes.
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var (secret, tokenId) = await MintStagerTokenAsync(client, engagementId);
            await SavePayloadAsync(host, tokenId, engagementId,
                endpoint: "https://front.example.com:8443", beaconEndpoint: "mtls.example.com:9443");
            var implantId = await EnrollAsync(client, secret);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.interact", Arguments = "" });

            issued.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task ChannelVerb_IsIssued_ForAStreamModeWebBuild()
    {
        // The web posture's interactive tier: a stream-mode build against a
        // web front dials the WebSocket beacon, so the baked mode stamps the
        // native carrier and the channel verb is claimable without any
        // split-socket naming.
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var (secret, tokenId) = await MintStagerTokenAsync(client, engagementId);
            await SavePayloadAsync(host, tokenId, engagementId,
                endpoint: "https://front.example.com:8443", beaconEndpoint: null, mode: "stream");
            var implantId = await EnrollAsync(client, secret);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.interact", Arguments = "" });

            issued.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task ChannelVerb_IsIssued_WhenNoBuildProfileResolved()
    {
        // A manually minted token names no payload, so the carrier set stays
        // undeclared: the permissive shape, where the dispatch-time claim
        // evaluation alone decides -- the behavior that predates the stamp.
        var (client, _, _) = AuthenticatedHost.Create();
        using (client)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var (secret, _) = await MintStagerTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.interact", Arguments = "" });

            issued.EnsureSuccessStatusCode();
        }
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Carrier Gate"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    private static async Task<(string Secret, Guid TokenId)> MintStagerTokenAsync(
        HttpClient client,
        string engagementId)
    {
        var response = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
        return (token!.Secret, Guid.Parse(token.StagerTokenId));
    }

    private static async Task<string> EnrollAsync(HttpClient client, string secret)
    {
        var response = await client.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null, PublicKey: null));
        response.EnsureSuccessStatusCode();
        var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
        return enrolled!.ImplantId!;
    }

    // The enroll-side derivation reads the payload record the token resolves
    // (Endpoint, BeaconEndpoint, the build profile's mode and fallbacks);
    // the fields it does not read are filler.
    private static async Task SavePayloadAsync(
        IHost host,
        Guid tokenId,
        string engagementId,
        string endpoint,
        string? beaconEndpoint,
        string? mode = null)
    {
        var payloads = host.Services.GetRequiredService<IPayloadStore>();
        await payloads.SaveAsync(new PayloadRecord(
            PayloadId: Guid.NewGuid(),
            EngagementId: Guid.Parse(engagementId),
            Class: "Stage2",
            Language: "dotnet",
            ContentType: "application/octet-stream",
            Fingerprint: "sha256:" + new string('a', 64),
            Content: Array.Empty<byte>(),
            Size: 0,
            BuiltAt: DateTimeOffset.UtcNow,
            Endpoint: endpoint,
            BeaconEndpoint: beaconEndpoint,
            TokenId: tokenId,
            Build: mode is null ? null : new PayloadBuildProfile { Mode = mode }));
    }
}
