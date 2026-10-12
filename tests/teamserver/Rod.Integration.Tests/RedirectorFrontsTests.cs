using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Listeners;
using Rod.CoreState.Redirectors;
using Rod.Transport.Listeners;

namespace Rod.Integration.Tests;

/// <summary>
/// The front management surface end to end (architecture.md Sec 8): an
/// enrolled node receives its forwarding rules as fronts attach and detach,
/// the listener's reported endpoint follows the primary front, and the
/// engagement trail records each change.
/// </summary>
public class RedirectorFrontsTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _client = null!;
    private Guid _engagementId;
    private IRedirectorFleet _fleet = null!;
    private IListenerRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        (_client, _host, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(_client);
        _fleet = _host.Services.GetRequiredService<IRedirectorFleet>();
        _registry = _host.Services.GetRequiredService<IListenerRegistry>();
        _engagementId = (await EngagementSetup.CreateOwnedEngagementAsync(_host)).Value;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>
    /// Seeds a listener into the registry without binding a socket -- fronts
    /// touch no sockets, so the registry entry is all the surface the flow
    /// needs.
    /// </summary>
    private async Task<Listener> SeedListenerAsync(string transport, string bind, string publicEndpoint)
    {
        var listener = Listener.Define(
            ListenerId.New(), $"{transport}-ingress", transport, bind, publicEndpoint,
            DateTimeOffset.UtcNow, engagementId: new EngagementId(_engagementId));
        await _registry.RegisterAsync(listener);
        return listener;
    }

    private static async Task<string> PostAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task AttachFront_PushesTheRuleAndRepointsThePrimary()
    {
        var listener = await SeedListenerAsync("https", "10.0.0.5:8443", "https://old.example.com:8443");
        var (socket, nodeId, _) = await RedirectorControlTestClient.EnrollAsync(_host, _fleet, AuthenticatedHost.GetOperatorId(_host));

        // A bare host completes against the listen port, the same completion
        // a listener's own public endpoint gets.
        var created = JsonSerializer.Deserialize<JsonElement>(await PostAsync(
            _client,
            $"/engagements/{_engagementId}/listeners/{listener.Id}/fronts",
            new { nodeId = nodeId.ToString(), listenEndpoint = "0.0.0.0:443", publicEndpoint = "c2.example.test", primary = true }));
        Assert.True(created.GetProperty("isPrimary").GetBoolean());

        // The rule arrives on the open control socket.
        var push = await RedirectorControlTestClient.ReceiveAsync(socket);
        Assert.Equal("rules", push.GetProperty("type").GetString());
        var rule = push.GetProperty("rules").GetProperty("rules")[0];
        Assert.Equal("tcp", rule.GetProperty("transport").GetString());
        Assert.Equal("0.0.0.0", rule.GetProperty("listenHost").GetString());
        Assert.Equal(443, rule.GetProperty("listenPort").GetInt32());
        Assert.Equal("10.0.0.5", rule.GetProperty("upstreamHost").GetString());
        Assert.Equal(8443, rule.GetProperty("upstreamPort").GetInt32());

        // The listener reports the primary front: the public endpoint builds
        // bake followed the attachment.
        var listed = JsonSerializer.Deserialize<JsonElement>(
            await _client.GetStringAsync($"/engagements/{_engagementId}/listeners"));
        var row = listed.EnumerateArray().Single(l => l.GetProperty("id").GetString() == listener.Id.ToString());
        Assert.Equal("https://c2.example.test:443", row.GetProperty("publicEndpoint").GetString());
        Assert.Equal(1, row.GetProperty("fronts").GetArrayLength());
        Assert.Equal(nodeId.ToString(), row.GetProperty("fronts")[0].GetProperty("nodeId").GetString());

        // And the engagement trail carries the change.
        var audit = JsonSerializer.Deserialize<JsonElement>(
            await _client.GetStringAsync($"/engagements/{_engagementId}/audit"));
        Assert.Contains(audit.GetProperty("items").EnumerateArray(),
            e => e.GetProperty("verb").GetString() == "listener.front-attached");

        socket.Dispose();
    }

    [Fact]
    public async Task PromoteAndDetach_TheEndpointFollowsAndTheRuleLeaves()
    {
        var listener = await SeedListenerAsync("https", "10.0.0.5:8443", "https://old.example.com:8443");
        var (socket, nodeId, _) = await RedirectorControlTestClient.EnrollAsync(_host, _fleet, AuthenticatedHost.GetOperatorId(_host));

        var first = JsonSerializer.Deserialize<JsonElement>(await PostAsync(
            _client, $"/engagements/{_engagementId}/listeners/{listener.Id}/fronts",
            new { nodeId = nodeId.ToString(), listenEndpoint = "0.0.0.0:443", publicEndpoint = "https://a.example.test" }));
        var second = JsonSerializer.Deserialize<JsonElement>(await PostAsync(
            _client, $"/engagements/{_engagementId}/listeners/{listener.Id}/fronts",
            new { nodeId = nodeId.ToString(), listenEndpoint = "0.0.0.0:8443", publicEndpoint = "https://b.example.test" }));
        _ = await RedirectorControlTestClient.ReceiveAsync(socket); // first rule push
        _ = await RedirectorControlTestClient.ReceiveAsync(socket); // second rule push

        // Promoting B makes it primary: builds bake B, the reported endpoint
        // follows, and no rule changes (rules are per front, not primarity).
        _ = await PostAsync(
            _client,
            $"/engagements/{_engagementId}/listeners/{listener.Id}/fronts/{second.GetProperty("frontId").GetString()}:primary",
            new { });
        var listed = JsonSerializer.Deserialize<JsonElement>(
            await _client.GetStringAsync($"/engagements/{_engagementId}/listeners"));
        Assert.Equal("https://b.example.test",
            listed.EnumerateArray().Single().GetProperty("publicEndpoint").GetString());

        // Detaching B promotes A back, and the node's rule for B leaves.
        var detach = await _client.DeleteAsync(
            $"/engagements/{_engagementId}/listeners/{listener.Id}/fronts/{second.GetProperty("frontId").GetString()}");
        Assert.Equal(HttpStatusCode.NoContent, detach.StatusCode);

        var push = await RedirectorControlTestClient.ReceiveAsync(socket);
        var rules = push.GetProperty("rules").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal(443, rules[0].GetProperty("listenPort").GetInt32());

        listed = JsonSerializer.Deserialize<JsonElement>(
            await _client.GetStringAsync($"/engagements/{_engagementId}/listeners"));
        Assert.Equal("https://a.example.test",
            listed.EnumerateArray().Single().GetProperty("publicEndpoint").GetString());

        socket.Dispose();
    }

    [Fact]
    public async Task AttachFront_RefusesTheShapesItMust()
    {
        var listener = await SeedListenerAsync("https", "10.0.0.5:8443", "https://old.example.com:8443");
        var (_, nodeId, _) = await RedirectorControlTestClient.EnrollAsync(_host, _fleet, AuthenticatedHost.GetOperatorId(_host));

        // Unknown node, malformed listen endpoint, and the dns family's
        // pending datagram relay each name their refusal.
        var unknown = await _client.PostAsJsonAsync(
            $"/engagements/{_engagementId}/listeners/{listener.Id}/fronts",
            new { nodeId = Guid.NewGuid().ToString(), listenEndpoint = "0.0.0.0:443" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var malformed = await _client.PostAsJsonAsync(
            $"/engagements/{_engagementId}/listeners/{listener.Id}/fronts",
            new { nodeId = nodeId.ToString(), listenEndpoint = "not-an-endpoint" });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        var dns = await SeedListenerAsync("dns", "10.0.0.5:5353", "zone.example.test");
        var dnsFront = await _client.PostAsJsonAsync(
            $"/engagements/{_engagementId}/listeners/{dns.Id}/fronts",
            new { nodeId = nodeId.ToString(), listenEndpoint = "0.0.0.0:53" });
        Assert.Equal(HttpStatusCode.BadRequest, dnsFront.StatusCode);

        // A port a node already binds, on any listener of any engagement, is
        // a conflict named with both sides.
        _ = await PostAsync(
            _client, $"/engagements/{_engagementId}/listeners/{listener.Id}/fronts",
            new { nodeId = nodeId.ToString(), listenEndpoint = "0.0.0.0:443", publicEndpoint = "https://a.example.test" });
        var other = await SeedListenerAsync("https", "10.0.0.5:9443", "https://other.example.com:9443");
        var clash = await _client.PostAsJsonAsync(
            $"/engagements/{_engagementId}/listeners/{other.Id}/fronts",
            new { nodeId = nodeId.ToString(), listenEndpoint = "0.0.0.0:443" });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
    }

    [Fact]
    public async Task FleetEndpoints_MintRevokeAndRetire()
    {
        var minted = JsonSerializer.Deserialize<JsonElement>(await PostAsync(
            _client, "/redirectors/credentials", new { name = "edge-fra-1" }));
        Assert.False(string.IsNullOrEmpty(minted.GetProperty("secret").GetString()));

        // The ledger shows the credential unspent; the secret never rides
        // there.
        var ledger = JsonSerializer.Deserialize<JsonElement>(
            await _client.GetStringAsync("/redirectors/credentials"));
        Assert.Null(ledger.EnumerateArray().Single().GetProperty("enrolledNodeId").GetString());

        var revoke = await _client.PostAsync(
            $"/redirectors/credentials/{minted.GetProperty("id").GetString()}:revoke", content: null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // Enroll-refusal of the revoked credential crosses the channel.
        var socket = RedirectorControlTestClient.Connect(_host);
        await RedirectorControlTestClient.SendAsync(socket, new { type = "enroll", credential = minted.GetProperty("secret").GetString() });
        Assert.Equal("revoked",
            (await RedirectorControlTestClient.ReceiveAsync(socket)).GetProperty("reason").GetString());
        socket.Dispose();

        // Retire closes the live session and refuses the next hello.
        var (live, nodeId, nodeSecret) = await RedirectorControlTestClient.EnrollAsync(_host, _fleet, AuthenticatedHost.GetOperatorId(_host));
        var roster = JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync("/redirectors"));
        Assert.True(roster.EnumerateArray().Single(n => n.GetProperty("id").GetString() == nodeId.ToString())
            .GetProperty("online").GetBoolean());

        var retire = await _client.PostAsync($"/redirectors/{nodeId}:retire", content: null);
        Assert.Equal(HttpStatusCode.NoContent, retire.StatusCode);

        var reconnect = RedirectorControlTestClient.Connect(_host);
        await RedirectorControlTestClient.SendAsync(reconnect, new { type = "hello", nodeId = nodeId.ToString(), nodeSecret });
        Assert.Equal("retired",
            (await RedirectorControlTestClient.ReceiveAsync(reconnect)).GetProperty("reason").GetString());
        reconnect.Dispose();
        live.Dispose();
    }
}
