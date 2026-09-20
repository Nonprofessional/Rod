using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Staging;

namespace Rod.Integration.Tests;

/// <summary>
/// The stage-2 fetch route (architecture.md Sec 6): the anonymous, pre-enroll
/// half of staging. A stage-1 stager presents the deployment credential its
/// own build baked and receives the stage-2 bytes for its engagement, each
/// served fetch spending one use of the token -- the download gate is the
/// credential's whole job, and the enrollment that follows rides the
/// credential baked into the fetched artifact. These checks pin the route's
/// contract: a valid token serves the stored bytes, a missing or wrong token
/// is refused, a payload outside the token's engagement does not exist as far
/// as the route is concerned, a single-use token serves exactly one fetch,
/// and a refused fetch never burns the budget.
/// </summary>
public class Stage2FetchTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed record FetchHarness(
        HttpClient Client,
        IHost Host,
        IStagerTokenService Tokens,
        EngagementId Engagement,
        StagerToken Token,
        Guid PayloadId,
        byte[] Content) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Host.Dispose();
            await ValueTask.CompletedTask;
        }
    }

    private static async Task<FetchHarness> SetupAsync()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        var engagements = host.Services.GetRequiredService<IEngagementRepository>();
        var tokens = host.Services.GetRequiredService<IStagerTokenService>();
        var payloads = host.Services.GetRequiredService<IPayloadStore>();

        var engagementId = EngagementId.New();
        var owner = OperatorId.New();
        await engagements.SaveAsync(Engagement.Create(engagementId, "fetch-test", owner, Now));
        var token = await tokens.MintAsync(engagementId, owner, Now);

        var content = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var payloadId = Guid.NewGuid();
        await payloads.SaveAsync(new PayloadRecord(
            payloadId, engagementId.Value, "Stage2", "DotNet",
            "application/octet-stream", "fingerprint", content, content.Length, Now));

        return new FetchHarness(client, host, tokens, engagementId, token, payloadId, content);
    }

    [Fact]
    public async Task ValidToken_ServesTheStoredBytes()
    {
        await using var h = await SetupAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/implants/stage2/{h.PayloadId}");
        request.Headers.Add("X-Stager-Token", h.Token.Secret);
        using var response = await h.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(h.Content, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MissingOrWrongToken_IsRefused()
    {
        await using var h = await SetupAsync();

        using var noHeader = await h.Client.GetAsync($"/implants/stage2/{h.PayloadId}");
        Assert.Equal(HttpStatusCode.Unauthorized, noHeader.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, $"/implants/stage2/{h.PayloadId}");
        wrong.Headers.Add("X-Stager-Token", "not-the-token");
        using var wrongResponse = await h.Client.SendAsync(wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongResponse.StatusCode);
    }

    [Fact]
    public async Task ForeignEngagementPayload_IsNotFound()
    {
        await using var h = await SetupAsync();

        // The same payload id stored against a different engagement: the
        // token's engagement cannot reach it, and the route does not reveal
        // that it exists at all.
        var payloads = h.Host.Services.GetRequiredService<IPayloadStore>();
        var engagements = h.Host.Services.GetRequiredService<IEngagementRepository>();
        var otherEngagement = EngagementId.New();
        await engagements.SaveAsync(Engagement.Create(otherEngagement, "other", OperatorId.New(), Now));
        await payloads.SaveAsync(new PayloadRecord(
            h.PayloadId, otherEngagement.Value, "Stage2", "DotNet",
            "application/octet-stream", "fingerprint", new byte[] { 1, 2, 3 }, 3, Now));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/implants/stage2/{h.PayloadId}");
        request.Headers.Add("X-Stager-Token", h.Token.Secret);
        using var response = await h.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EachServedFetch_SpendsOneUse()
    {
        await using var h = await SetupAsync();

        using var first = new HttpRequestMessage(HttpMethod.Get, $"/implants/stage2/{h.PayloadId}");
        first.Headers.Add("X-Stager-Token", h.Token.Secret);
        using var firstResponse = await h.Client.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // The single use is spent by the served download: a second fetch of
        // the same credential is refused. The enrollment that follows rides
        // the credential baked into the fetched bytes, never this one.
        using var second = new HttpRequestMessage(HttpMethod.Get, $"/implants/stage2/{h.PayloadId}");
        second.Headers.Add("X-Stager-Token", h.Token.Secret);
        using var secondResponse = await h.Client.SendAsync(second);
        Assert.Equal(HttpStatusCode.Unauthorized, secondResponse.StatusCode);
    }

    [Fact]
    public async Task ARefusedFetch_LeavesTheBudgetWhole()
    {
        await using var h = await SetupAsync();

        // A payload id this engagement cannot see: the route refuses without
        // serving, and the refusal must not burn the only use.
        using var refused = new HttpRequestMessage(HttpMethod.Get, $"/implants/stage2/{Guid.NewGuid()}");
        refused.Headers.Add("X-Stager-Token", h.Token.Secret);
        using var refusedResponse = await h.Client.SendAsync(refused);
        Assert.Equal(HttpStatusCode.NotFound, refusedResponse.StatusCode);

        // The real fetch still serves on the same single-use credential.
        using var served = new HttpRequestMessage(HttpMethod.Get, $"/implants/stage2/{h.PayloadId}");
        served.Headers.Add("X-Stager-Token", h.Token.Secret);
        using var servedResponse = await h.Client.SendAsync(served);
        Assert.Equal(HttpStatusCode.OK, servedResponse.StatusCode);
    }
}
