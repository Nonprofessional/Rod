using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Operators;

namespace Rod.Transport.Payloads;

/// <summary>
/// The shared completion step of every payload build: store the bytes for
/// retrieval, append the <see cref="AuditEventKind.PayloadBuilt"/> fact to the
/// engagement trail, and shape the operator response. Both build paths -- the
/// synchronous build endpoint and the background job runner -- finish a build
/// through this one method so a completed payload is recorded identically
/// wherever it was requested from.
/// </summary>
internal static class PayloadBuildRecorder
{
    public static async Task<Endpoints.PayloadEndpoints.BuildPayloadResponse> RecordAsync(
        BuildArtifact artifact,
        OperatorId requestedBy,
        IPayloadStore payloads,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        // The built bytes are stored for retrieval, then the build is recorded
        // (architecture.md Sec 6/11): a PayloadBuilt audit event carrying the
        // class/config, the artifact fingerprint, and -- when the transform
        // chain ran -- the name of every applied transform (Sec 6, the
        // transform seam), so the trail proves which transforms produced the
        // stored bytes. No implant or task is bound yet, so those ids are
        // unused. The store stamps the chain hashes on append; the call site
        // supplies only the facts.
        var transformTrail = artifact.Transforms.Count == 0
            ? ""
            : " transforms=" + string.Join(
                ">",
                artifact.Transforms.Select(t => t.Metadata is null ? t.Name : $"{t.Name}({t.Metadata})"));
        // The baked enrollment credential is part of the build's story: the
        // trail names the token id (never the secret) so a later revocation
        // lines up with the artifact that carried it.
        var tokenTrail = artifact.Params.TokenId is { } tokenId ? $" token={tokenId.ToString()[..8]}" : "";
        // The split-socket shape names its second front: enroll dials the
        // endpoint above, the beacon the one here.
        var beaconTrail = artifact.Params.Transport.BeaconEndpoint is { } beaconEndpoint
            ? $" beacon={beaconEndpoint}"
            : "";
        await payloads.SaveAsync(
            new PayloadRecord(
                artifact.ArtifactId,
                artifact.EngagementId.Value,
                artifact.Class.ToString(),
                artifact.Language.ToString(),
                artifact.ContentType,
                artifact.Fingerprint,
                artifact.Content,
                artifact.Size,
                artifact.BuiltAt,
                Target: $"{artifact.Params.Target.OperatingSystem}/{artifact.Params.Target.Architecture}",
                Endpoint: artifact.Params.Transport.Endpoint,
                BeaconEndpoint: artifact.Params.Transport.BeaconEndpoint,
                TokenId: artifact.Params.TokenId,
                EnvelopeKeyId: artifact.Params.EnvelopeKeyId,
                EnvelopeKey: artifact.Params.EnvelopeKey,
                Build: new PayloadBuildProfile
                {
                    Mode = artifact.Params.Beacon.Mode,
                    SleepSeconds = artifact.Params.Beacon.Sleep.TotalSeconds,
                    JitterSeconds = artifact.Params.Beacon.Jitter.TotalSeconds,
                    KillDate = artifact.Params.Beacon.KillDate,
                    TokenMaxUses = artifact.Params.TokenMaxUses,
                    EnrollPath = artifact.Params.Transport.EnrollPath,
                    UserAgent = artifact.Params.Transport.UserAgent,
                    RequestTimeoutSeconds = artifact.Params.Transport.RequestTimeout.TotalSeconds,
                    Envelope = artifact.Params.Transport.Envelope.ToString(),
                    CheckInProtection = artifact.Params.Transport.CheckInProtection,
                    FallbackEndpoints = artifact.Params.Transport.FallbackEndpoints.Count == 0
                        ? null
                        : artifact.Params.Transport.FallbackEndpoints.ToArray(),
                }),
            cancellationToken);
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: artifact.EngagementId.Value,
                operatorId: requestedBy.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "payload.build",
                kind: AuditEventKind.PayloadBuilt,
                payload: $"{artifact.Language}:{artifact.Params.Target.OperatingSystem}/{artifact.Params.Target.Architecture} {artifact.Params.Transport.Endpoint}{beaconTrail}{transformTrail}{tokenTrail}",
                output: null,
                outcome: artifact.Fingerprint,
                at: artifact.BuiltAt),
            cancellationToken);

        return new Endpoints.PayloadEndpoints.BuildPayloadResponse(
            artifact.ArtifactId.ToString(),
            artifact.EngagementId.ToString(),
            artifact.Class.ToString(),
            artifact.Language.ToString(),
            artifact.ContentType,
            artifact.Size,
            artifact.Fingerprint,
            artifact.BuiltAt,
            artifact.Transforms.Select(t => t.Name).ToArray(),
            TokenId: artifact.Params.TokenId?.ToString());
    }
}
