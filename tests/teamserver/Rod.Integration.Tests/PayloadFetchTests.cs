using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Deployment;

namespace Rod.Integration.Tests;

/// <summary>
/// The payload fetch route (architecture.md Sec 6): the anonymous, pre-enroll
/// half of deployment. A launcher one-liner presents the deployment credential
/// its render minted and receives the payload bytes for its engagement, each
/// served fetch spending one use of the credential -- the download gate is the
/// credential's whole job, and the enrollment that follows rides the
/// credential baked into the fetched artifact. These checks pin the route's
/// contract: a valid credential serves the stored bytes, a missing or wrong
/// one is refused, a payload outside the credential's engagement does not
/// exist as far as the route is concerned, a single-use credential serves
/// exactly one fetch, and a refused fetch never burns the budget. Every
/// fetch a resolvable credential gate-keeps lands on the audit trail --
/// served or refused -- while an unknown secret leaves no record.
/// </summary>
public class PayloadFetchTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed record FetchHarness(
        HttpClient Client,
        IHost Host,
        IDeployTokenService Tokens,
        EngagementId Engagement,
        DeployToken Token,
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
        var tokens = host.Services.GetRequiredService<IDeployTokenService>();
        var payloads = host.Services.GetRequiredService<IPayloadStore>();

        var engagementId = EngagementId.New();
        var owner = OperatorId.New();
        await engagements.SaveAsync(Engagement.Create(engagementId, "fetch-test", owner, Now));
        var token = await tokens.MintAsync(engagementId, owner, Now);

        var content = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var payloadId = Guid.NewGuid();
        await payloads.SaveAsync(new PayloadRecord(
            payloadId, engagementId.Value, "Implant", "DotNet",
            "application/octet-stream", "fingerprint", content, content.Length, Now));

        return new FetchHarness(client, host, tokens, engagementId, token, payloadId, content);
    }

    [Fact]
    public async Task ValidToken_ServesTheStoredBytes()
    {
        await using var h = await SetupAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/implants/payloads/{h.PayloadId}");
        request.Headers.Add("X-Deploy-Token", h.Token.Secret);
        using var response = await h.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(h.Content, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MissingOrWrongToken_IsRefused()
    {
        await using var h = await SetupAsync();

        using var noHeader = await h.Client.GetAsync($"/implants/payloads/{h.PayloadId}");
        Assert.Equal(HttpStatusCode.Unauthorized, noHeader.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, $"/implants/payloads/{h.PayloadId}");
        wrong.Headers.Add("X-Deploy-Token", "not-the-token");
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
            h.PayloadId, otherEngagement.Value, "Implant", "DotNet",
            "application/octet-stream", "fingerprint", new byte[] { 1, 2, 3 }, 3, Now));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/implants/payloads/{h.PayloadId}");
        request.Headers.Add("X-Deploy-Token", h.Token.Secret);
        using var response = await h.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EachServedFetch_SpendsOneUse()
    {
        await using var h = await SetupAsync();

        using var first = new HttpRequestMessage(HttpMethod.Get, $"/implants/payloads/{h.PayloadId}");
        first.Headers.Add("X-Deploy-Token", h.Token.Secret);
        using var firstResponse = await h.Client.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // The single use is spent by the served download: a second fetch of
        // the same credential is refused. The enrollment that follows rides
        // the credential baked into the fetched bytes, never this one.
        using var second = new HttpRequestMessage(HttpMethod.Get, $"/implants/payloads/{h.PayloadId}");
        second.Headers.Add("X-Deploy-Token", h.Token.Secret);
        using var secondResponse = await h.Client.SendAsync(second);
        Assert.Equal(HttpStatusCode.Unauthorized, secondResponse.StatusCode);
    }

    [Fact]
    public async Task ARefusedFetch_LeavesTheBudgetWhole()
    {
        await using var h = await SetupAsync();

        // A payload id this engagement cannot see: the route refuses without
        // serving, and the refusal must not burn the only use.
        using var refused = new HttpRequestMessage(HttpMethod.Get, $"/implants/payloads/{Guid.NewGuid()}");
        refused.Headers.Add("X-Deploy-Token", h.Token.Secret);
        using var refusedResponse = await h.Client.SendAsync(refused);
        Assert.Equal(HttpStatusCode.NotFound, refusedResponse.StatusCode);

        // The real fetch still serves on the same single-use credential.
        using var served = new HttpRequestMessage(HttpMethod.Get, $"/implants/payloads/{h.PayloadId}");
        served.Headers.Add("X-Deploy-Token", h.Token.Secret);
        using var servedResponse = await h.Client.SendAsync(served);
        Assert.Equal(HttpStatusCode.OK, servedResponse.StatusCode);
    }

    [Fact]
    public async Task EachServedFetch_IsRecordedOnTheTrailAndPushedLive()
    {
        await using var h = await SetupAsync();

        // Subscribe before the fetch: the live frame rides the operator bus,
        // so it must reach a subscriber that was already listening.
        var bus = h.Host.Services.GetRequiredService<ILiveEventBus>();
        await using var events = bus.SubscribeAsync(h.Engagement).GetAsyncEnumerator();
        var next = events.MoveNextAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/implants/payloads/{h.PayloadId}");
        request.Headers.Add("X-Deploy-Token", h.Token.Secret);
        request.Headers.Add("User-Agent", "Wget/1.21.2 (linux-gnu)");
        using var response = await h.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The serve is the deployment's first observable footprint: the
        // fetcher carries no Rod identity yet, so the trail gets a
        // system-attributed fact carrying what the wire showed -- and the
        // operator layer hears the same frame live, so a launcher row's
        // remaining budget moves without an operator pressing refresh.
        var audit = h.Host.Services.GetRequiredService<IAuditStore>();
        var trail = await audit.ListAsync(h.Engagement.Value);
        var fact = Assert.Single(trail, e => e.Kind == AuditEventKind.PayloadFetched);
        Assert.Equal(OperatorId.Empty.Value, fact.OperatorId);
        Assert.Contains("remote=", fact.Payload);
        Assert.Contains("ua=Wget/1.21.2 (linux-gnu)", fact.Payload);
        Assert.Contains($"payload={h.PayloadId:N}", fact.Payload);
        Assert.Contains($"token={h.Token.Id}", fact.Payload);
        Assert.Equal("served", fact.Outcome);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.True(await next.AsTask().WaitAsync(timeout.Token));
        Assert.Equal(LiveEventKind.PayloadFetched, events.Current.Kind);
        Assert.Contains("ua=Wget/1.21.2 (linux-gnu)", events.Current.Payload);

        // The budget-burned retry is exactly the fact an operator wants while
        // the launcher row lives: the spent credential still resolves, so the
        // refusal lands on the trail with its reason -- the wire answer stays
        // uniform, the record does not.
        using var refused = new HttpRequestMessage(HttpMethod.Get, $"/implants/payloads/{h.PayloadId}");
        refused.Headers.Add("X-Deploy-Token", h.Token.Secret);
        using var refusedResponse = await h.Client.SendAsync(refused);
        Assert.Equal(HttpStatusCode.Unauthorized, refusedResponse.StatusCode);
        var afterRefusal = await audit.ListAsync(h.Engagement.Value);
        var refusal = Assert.Single(afterRefusal, e =>
            e.Kind == AuditEventKind.PayloadFetched && e.Outcome != "served");
        Assert.Equal("refused:spent", refusal.Outcome);
        Assert.Contains($"token={h.Token.Id}", refusal.Payload);
    }

    [Fact]
    public async Task ARevokedCredential_RefusalIsRecorded_AnUnknownSecretIsNot()
    {
        await using var h = await SetupAsync();
        var audit = h.Host.Services.GetRequiredService<IAuditStore>();
        var tokens = h.Host.Services.GetRequiredService<IDeployTokenService>();

        // The soft kill: the row stays resolvable, so the attempt on the
        // revoked credential is a fact with a reason.
        Assert.True(await tokens.RevokeAsync(h.Token.Id));
        using var revoked = new HttpRequestMessage(HttpMethod.Get, $"/implants/payloads/{h.PayloadId}");
        revoked.Headers.Add("X-Deploy-Token", h.Token.Secret);
        using var revokedResponse = await h.Client.SendAsync(revoked);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);

        var trail = await audit.ListAsync(h.Engagement.Value);
        var refusal = Assert.Single(trail, e => e.Kind == AuditEventKind.PayloadFetched);
        Assert.Equal("refused:revoked", refusal.Outcome);

        // An unknown secret belongs to no engagement: no fact, nothing to
        // attribute the attempt to.
        using var unknown = new HttpRequestMessage(HttpMethod.Get, $"/implants/payloads/{h.PayloadId}");
        unknown.Headers.Add("X-Deploy-Token", "not-any-credential");
        using var unknownResponse = await h.Client.SendAsync(unknown);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownResponse.StatusCode);
        var afterUnknown = await audit.ListAsync(h.Engagement.Value);
        Assert.Single(afterUnknown, e => e.Kind == AuditEventKind.PayloadFetched);
    }

    [Fact]
    public async Task ADeletedLaunchersCredential_ResolvesToNothingAndLeavesNoRecord()
    {
        await using var h = await SetupAsync();
        var audit = h.Host.Services.GetRequiredService<IAuditStore>();
        var tokens = h.Host.Services.GetRequiredService<IDeployTokenService>();

        // The hard kill that rides a launcher row's deletion: the resolution
        // itself goes, and later attempts on the secret read as nobody's.
        Assert.True(await tokens.DeleteAsync(h.Token.Id));
        using var deleted = new HttpRequestMessage(HttpMethod.Get, $"/implants/payloads/{h.PayloadId}");
        deleted.Headers.Add("X-Deploy-Token", h.Token.Secret);
        using var deletedResponse = await h.Client.SendAsync(deleted);
        Assert.Equal(HttpStatusCode.Unauthorized, deletedResponse.StatusCode);

        var trail = await audit.ListAsync(h.Engagement.Value);
        Assert.DoesNotContain(trail, e => e.Kind == AuditEventKind.PayloadFetched);
    }
}
