using System.Net;
using System.Net.Http.Json;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// The TLS trust posture's build-request gate (architecture.md Sec 9):
/// 'pinned' is the default posture every request keeps, 'public' is the
/// real-domain departure, anything else is a typo the build must refuse
/// rather than silently pin, and public rides https dials alone -- a
/// cleartext or socket front has no handshake to carry the posture.
/// </summary>
public class TlsTrustBuildTests
{
    private sealed class EngagementBody
    {
        public string EngagementId { get; set; } = "";
    }

    [Fact]
    public async Task AnUnknownTrustSpellingIsRefused()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using var hostScope = host;
        using var clientScope = client;
        await AuthenticatedHost.LoginAsync(client);

        var engagement = await client.PostAsJsonAsync("/engagements", new { Name = "trust-spelling" });
        engagement.EnsureSuccessStatusCode();

        // The refusal precedes endpoint resolution, so no listener is needed
        // to see it.
        var created = await engagement.Content.ReadFromJsonAsync<EngagementBody>();
        var refused = await client.PostAsJsonAsync(
            $"/engagements/{created!.EngagementId}/payloads",
            new { Trust = "bogus" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var text = await refused.Content.ReadAsStringAsync();
        Assert.Contains("Trust must be 'pinned' (the default) or 'public'.", text);
    }

    [Fact]
    public async Task PublicTrustIsRefusedOnAFrontWithoutAnHttpsDial()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using var hostScope = host;
        using var clientScope = client;
        await AuthenticatedHost.LoginAsync(client);

        var engagement = await client.PostAsJsonAsync("/engagements", new { Name = "trust-front" });
        engagement.EnsureSuccessStatusCode();
        var created = await engagement.Content.ReadFromJsonAsync<EngagementBody>();

        // A socket front: legitimate to build against, but it carries no TLS
        // handshake, so the public posture has nothing to ride.
        var port = TestSupport.GetFreeTcpPort();
        var listener = await client.PostAsJsonAsync(
            $"/engagements/{created!.EngagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "trust-tcp",
                Transport: "tcp",
                BindAddress: $"127.0.0.1:{port}",
                PublicEndpoint: $"127.0.0.1:{port}"));
        listener.EnsureSuccessStatusCode();
        var front = await listener.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();

        var refused = await client.PostAsJsonAsync(
            $"/engagements/{created.EngagementId}/payloads",
            new { ListenerId = front!.Id, Trust = "public" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var text = await refused.Content.ReadAsStringAsync();
        Assert.Contains("Trust 'public' rides TLS fronts", text);
    }
}
