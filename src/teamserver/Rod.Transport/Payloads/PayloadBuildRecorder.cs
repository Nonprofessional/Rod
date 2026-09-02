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
                artifact.BuiltAt),
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
                payload: $"{artifact.Language}:{artifact.Params.Target.OperatingSystem}/{artifact.Params.Target.Architecture} {artifact.Params.Transport.Endpoint}{transformTrail}",
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
            artifact.Transforms.Select(t => t.Name).ToArray());
    }
}
