using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Deployment;

namespace Rod.Transport.Payloads;

/// <summary>
/// Mints the enrollment credential a payload build bakes into its artifact.
/// The operator never sees the secret: it travels from the mint straight into
/// the baked profile, and the build response reports only the token's id --
/// enough to revoke it, never enough to reuse it. Both build paths (the
/// synchronous endpoint and the background job) mint through this one step.
///
/// Defaults follow the artifact, not the manual mint's one-hour window: a
/// single use (one artifact, one enrollment) inside the artifact's own kill
/// window when one is pinned -- the credential lives as long as the artifact
/// it rides and no longer -- and 30 days on an open-ended build (no kill
/// date), because an implant that may run for years is no reason to leave a
/// dropped credential redeemable for them. The mint is attributed to the
/// engagement's owner (the service requires it; the owner authorizes the
/// deployment channel) while the build's own audit fact attributes the build
/// to its requester -- together the trail names both.
/// </summary>
internal static class PayloadBuildTokenMinter
{
    // The fall-back window for an open-ended build (no kill date pinned): the
    // credential stays redeemable for 30 days, independent of the implant's
    // own unlimited run.
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(30);

    public static async Task<(string Secret, DeployTokenId Id)> MintAsync(
        Engagement engagement,
        Endpoints.PayloadEndpoints.BuildPayloadRequest body,
        IDeployTokenService tokens,
        TimeProvider clock,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var maxUses = body.TokenMaxUses ?? 1;
        // The window follows the artifact when one is pinned: the credential
        // lives as long as the artifact it rides and no longer. An open-ended
        // build (no kill date) keeps the 30-day collection default instead --
        // an implant may run for years, but a dropped credential should not
        // stay redeemable for them.
        var killDate = PayloadBuildService.ResolveKillDate(now, body.KillDate);
        var lifetime = body.TokenLifetimeSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : killDate - now ?? DefaultLifetime;
        var token = await tokens.MintAsync(engagement.Id, engagement.OwnerId, now, maxUses, lifetime, cancellationToken: cancellationToken);

        // The same fact a manual mint records (architecture.md Sec 11): the
        // payload names the baked shape so the trail shows this token never
        // passed through an operator's hands.
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagement.Id.Value,
                operatorId: engagement.OwnerId.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "mint-deploy-token",
                kind: AuditEventKind.DeployTokenMinted,
                payload: $"bakedIntoPayload maxUses={token.MaxUses} expiresAt={token.ExpiresAt:O}",
                output: null,
                outcome: token.Id.ToString(),
                at: token.IssuedAt),
            cancellationToken);

        return (token.Secret, token.Id);
    }
}
