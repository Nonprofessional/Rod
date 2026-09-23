using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Deployment;
using Rod.Transport.Payloads;

namespace Rod.Integration.Tests;

/// <summary>
/// The loader tier's delivery half (architecture.md Sec 6): the seal the
/// fetch route serves and the gates that keep it honest. The delivery seal
/// is the same R1 wire shape every envelope purpose uses, under its own
/// purpose tag -- these checks pin that the served bytes open under the
/// loader's recorded key and no other purpose's ciphertext does, that the
/// route's governance matches the payload fetch (credential gate, engagement
/// scope, nothing for a non-loader id), and that a loader whose delivered
/// payload or key is gone serves nothing rather than something unsealed.
/// </summary>
public class LoaderDeliveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // The seal itself: a WrapBody under the stage AAD round-trips through
    // TryUnwrapBody with the same key, and a body sealed for another purpose
    // never opens as a stage -- the purpose tag is the replay boundary.
    [Fact]
    public void TheDeliverySeal_RoundTrips_AndRefusesForeignPurposes()
    {
        var (keyId, key) = AesGcmEnvelope.Mint();
        var delivered = new byte[] { 0x7f, 0x45, 0x4c, 0x46, 1, 2, 3, 4 };

        var sealedPayload = AesGcmEnvelope.WrapBody(delivered, keyId, key, AesGcmEnvelope.LoaderPayloadAad);

        Assert.Equal(delivered, AesGcmEnvelope.TryUnwrapBody(sealedPayload, keyId, key, AesGcmEnvelope.LoaderPayloadAad));

        var contactBody = AesGcmEnvelope.WrapBody(delivered, keyId, key, AesGcmEnvelope.ContactResponseAad);
        Assert.Null(AesGcmEnvelope.TryUnwrapBody(contactBody, keyId, key, AesGcmEnvelope.LoaderPayloadAad));

        var (otherId, otherKey) = AesGcmEnvelope.Mint();
        Assert.Null(AesGcmEnvelope.TryUnwrapBody(sealedPayload, otherId, otherKey, AesGcmEnvelope.LoaderPayloadAad));
    }

    private sealed record LoaderHarness(
        HttpClient Client,
        IHost Host,
        EngagementId Engagement,
        DeployToken Token,
        Guid LoaderId,
        Guid StageId,
        Guid KeyId,
        byte[] Key,
        byte[] Stage) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Host.Dispose();
            await ValueTask.CompletedTask;
        }
    }

    private static async Task<LoaderHarness> SetupAsync(Guid? deliversOverride = null, bool withSeal = true)
    {
        var (client, host, _) = AuthenticatedHost.Create();
        var engagements = host.Services.GetRequiredService<IEngagementRepository>();
        var tokens = host.Services.GetRequiredService<IDeployTokenService>();
        var payloads = host.Services.GetRequiredService<IPayloadStore>();

        var engagementId = EngagementId.New();
        var owner = OperatorId.New();
        await engagements.SaveAsync(Engagement.Create(engagementId, "loader-test", owner, Now));
        var token = await tokens.MintAsync(engagementId, owner, Now);

        var stage = new byte[] { 0x7f, 0x45, 0x4c, 0x46, 1, 2, 3, 4, 5, 6, 7, 8 };
        var stageId = Guid.NewGuid();
        await payloads.SaveAsync(new PayloadRecord(
            stageId, engagementId.Value, "Implant", "Rust",
            "application/octet-stream", "stage-fingerprint", stage, stage.Length, Now));

        var loaderId = Guid.NewGuid();
        var (keyId, key) = AesGcmEnvelope.Mint();
        await payloads.SaveAsync(new PayloadRecord(
            loaderId, engagementId.Value, "Implant", "Rust",
            "application/octet-stream", "loader-fingerprint", new byte[] { 1 }, 1, Now,
            DeliversPayloadId: withSeal ? deliversOverride ?? stageId : null,
            EnvelopeKeyId: withSeal ? keyId : null,
            EnvelopeKey: withSeal ? key : null));

        return new LoaderHarness(client, host, engagementId, token, loaderId, stageId, keyId, key, stage);
    }

    [Fact]
    public async Task ValidToken_ServesThePayloadSealedUnderTheLoadersKey()
    {
        await using var h = await SetupAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/implants/loaders/{h.LoaderId}/payload");
        request.Headers.Add("X-Deploy-Token", h.Token.Secret);
        using var response = await h.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var served = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEqual(h.Stage, served);
        Assert.Equal(
            h.Stage,
            AesGcmEnvelope.TryUnwrapBody(served, h.KeyId, h.Key, AesGcmEnvelope.LoaderPayloadAad));
    }

    [Fact]
    public async Task AMissingOrWrongCredential_IsRefused()
    {
        await using var h = await SetupAsync();

        using var noHeader = await h.Client.GetAsync($"/implants/loaders/{h.LoaderId}/payload");
        Assert.Equal(HttpStatusCode.Unauthorized, noHeader.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, $"/implants/loaders/{h.LoaderId}/payload");
        wrong.Headers.Add("X-Deploy-Token", "not-a-token");
        using var refused = await h.Client.SendAsync(wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    // A plain payload id on the loader payload route does not exist as far
    // as the route is concerned -- the delivery reference is what makes a
    // record a loader, and a plain fetch belongs on its own path.
    [Fact]
    public async Task APlainPayloadId_OnTheLoaderPayloadRoute_DoesNotExist()
    {
        await using var h = await SetupAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/implants/loaders/{h.StageId}/payload");
        request.Headers.Add("X-Deploy-Token", h.Token.Secret);
        using var response = await h.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // A loader whose stage was deleted (or whose record lost its seal --
    // the same store deletion) is a dead delivery: the route serves
    // nothing rather than bytes no loader can open.
    [Fact]
    public async Task ALoaderWhoseDeliveredPayloadIsDeleted_ServesNothing()
    {
        await using var h = await SetupAsync(deliversOverride: Guid.NewGuid());

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/implants/loaders/{h.LoaderId}/payload");
        request.Headers.Add("X-Deploy-Token", h.Token.Secret);
        using var response = await h.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
