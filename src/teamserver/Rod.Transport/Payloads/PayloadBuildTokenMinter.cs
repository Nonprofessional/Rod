using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Staging;

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
/// window, resolved exactly as the build resolves it -- the credential lives
/// as long as the artifact it rides and no longer. The mint is attributed to
/// the engagement's owner (the service requires it; the owner authorizes the
/// deployment channel) while the build's own audit fact attributes the build
/// to its requester -- together the trail names both.
/// </summary>
internal static class PayloadBuildTokenMinter
{
    public static async Task<(string Secret, StagerTokenId Id)> MintAsync(
        Engagement engagement,
        Endpoints.PayloadEndpoints.BuildPayloadRequest body,
        IStagerTokenService tokens,
        TimeProvider clock,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var maxUses = body.TokenMaxUses ?? 1;
        var lifetime = body.TokenLifetimeSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : PayloadBuildService.ResolveKillDate(now, body.KillDate) - now;

        var token = await tokens.MintAsync(engagement.Id, engagement.OwnerId, now, maxUses, lifetime, cancellationToken);

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
                verb: "mint-stager-token",
                kind: AuditEventKind.StagerTokenMinted,
                payload: $"bakedIntoPayload maxUses={token.MaxUses} expiresAt={token.ExpiresAt:O}",
                output: null,
                outcome: token.Id.ToString(),
                at: token.IssuedAt),
            cancellationToken);

        return (token.Secret, token.Id);
    }
}
