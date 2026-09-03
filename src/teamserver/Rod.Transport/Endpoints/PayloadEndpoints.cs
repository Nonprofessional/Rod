using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Rod.Audit;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.Transport.Payloads;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing payload-build endpoints: an operator
/// requests a payload for an engagement, the build pipeline invokes the
/// language's build unit, the bytes are stored in the payload store, and the
/// fingerprinted artifact is recorded into the audit trail. The build response's
/// Location resolves to a download route that returns the compiled bytes, so a
/// built payload is retrievable, not just recorded -- the acceptance point.
///
/// Scoped by engagement (architecture.md Sec 3): the engagement id in the path
/// binds the build. The endpoint composes the audit write, mirroring how task
/// completion is recorded on the beacon stream (architecture.md Sec 11): the
/// build service is audit-agnostic by design, the transport layer is where the
/// build meets the audit trail. No implant is enrolled at build time, so the
/// audit event carries only the engagement and the requesting operator.
/// </summary>
public static class PayloadEndpoints
{
    public static IEndpointRouteBuilder MapPayloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Operator-facing: a payload build requires an authenticated operator session.
        var group = endpoints.MapGroup("/engagements/{engagementId}/payloads").RequireAuthorization();

        group.MapPost("/", BuildAsync).WithName(nameof(BuildAsync));
        group.MapGet("/", ListPayloadsAsync).WithName(nameof(ListPayloadsAsync));
        group.MapGet("/{artifactId}", DownloadAsync).WithName(nameof(DownloadAsync));
        group.MapDelete("/{artifactId}", DeleteAsync).WithName(nameof(DeleteAsync));

        return endpoints;
    }

    // The durable library view: the payload store's own listing, which
    // survives restarts and outlives the bounded, process-local build-job
    // list. An operator who needs the artifact built three weeks ago -- to
    // download it again, revoke its baked credential, or delete it so a
    // deployed stager's fetch stops answering -- finds it here.
    private static async Task<IResult> ListPayloadsAsync(
        string engagementId,
        IEngagementRepository engagements,
        IPayloadStore payloads,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        if (await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken) is null)
            return Results.NotFound(new Problem("Engagement does not exist."));

        var records = await payloads.ListAsync(engagementValue, cancellationToken);
        return Results.Ok(records.Select(PayloadSummaryResponse.Of).ToArray());
    }

    // Deletes a stored payload: the bytes and the library entry are gone and a
    // stager fetching it 404s from now on. The deletion is audited -- the trail
    // names what was removed -- and revoking the baked credential stays its own
    // action on the library row.
    private static async Task<IResult> DeleteAsync(
        string engagementId,
        string artifactId,
        ClaimsPrincipal user,
        IEngagementRepository engagements,
        IPayloadStore payloads,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var requestedBy = user.TryGetOperatorId();
        if (requestedBy is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(artifactId, out var artifactValue))
            return Results.BadRequest(new Problem("Artifact id is not a valid identifier."));

        if (await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken) is null)
            return Results.NotFound(new Problem("Engagement does not exist."));

        // Resolve first so the audit fact carries the payload's own metadata;
        // the store's removal is the state change it records.
        var payload = await payloads.FindAsync(artifactValue, engagementValue, cancellationToken);
        if (payload is null)
            return Results.NotFound(new Problem("Payload does not exist in this engagement."));

        if (!await payloads.RemoveAsync(artifactValue, engagementValue, cancellationToken))
            return Results.NotFound(new Problem("Payload does not exist in this engagement."));

        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagementValue,
                operatorId: requestedBy.Value.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "payload.delete",
                kind: AuditEventKind.PayloadDeleted,
                payload: $"{payload.Language}:{payload.Class} {payload.Target ?? "unknown-target"} {payload.Endpoint ?? "unknown-endpoint"}",
                output: null,
                outcome: payload.Fingerprint,
                at: clock.GetUtcNow()),
            cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> BuildAsync(
        string engagementId,
        BuildPayloadRequest body,
        ClaimsPrincipal user,
        PayloadBuildService builds,
        IEngagementRepository engagements,
        IPayloadStore payloads,
        Rod.Transport.Listeners.IListenerRegistry listeners,
        Rod.CoreState.Staging.IStagerTokenService tokens,
        TimeProvider clock,
        IAuditStore audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // The requesting operator is the authenticated operator, resolved off the
        // session principal rather than named in the body (operator auth).
        var requestedBy = user.TryGetOperatorId();
        if (requestedBy is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        // The engagement must exist before anything is built: a build for a
        // bogus engagement would otherwise produce and audit an artifact against
        // an engagement with no other record.
        var engagement = await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken);
        if (engagement is null)
            return Results.NotFound(new Problem("Engagement does not exist."));

        // The request body parses and validates exactly as the background job
        // path does (the shared parser): same refusals, same defaults, same
        // stager stage-2 resolution, same listener-name endpoint resolution.
        var (parsed, parseError) = await PayloadBuildRequestParser.ParseAsync(
            body, new EngagementId(engagementValue), requestedBy.Value, payloads, listeners, cancellationToken);
        if (parseError is not null)
            return Results.BadRequest(new Problem(parseError));

        // The build's enrollment credential is minted here and baked into the
        // artifact -- the operator never handles the secret. Both build paths
        // mint identically.
        var (secret, tokenId) = await PayloadBuildTokenMinter.MintAsync(
            engagement!, body, tokens, clock, audit, cancellationToken);
        var request = parsed! with { TokenSecret = secret, MintedTokenId = tokenId.Value };

        BuildArtifact artifact;
        try
        {
            artifact = await builds.BuildAsync(request!, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            // No build unit for the requested language (or another contract
            // failure): an operator mistake, not a server fault.
            return Results.BadRequest(new Problem(ex.Message));
        }
        catch (Exception ex)
        {
            // The build unit failed (toolchain error, disk, cancellation of a
            // child process). Keep the response generic; the server log carries
            // the detail.
            loggerFactory.CreateLogger("Rod.Transport.Endpoints.PayloadEndpoints")
                .LogError(ex, "Payload build failed for language {Language}.", request!.Language);
            return Results.Problem(
                title: "Payload build failed.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // The bytes are stored and the build recorded into the trail through
        // the shared completion step -- identical to the background job path.
        var response = await PayloadBuildRecorder.RecordAsync(
            artifact, requestedBy.Value, payloads, audit, cancellationToken);

        return Results.Created(
            $"/engagements/{response.EngagementId}/payloads/{response.ArtifactId}",
            response);
    }

    private static async Task<IResult> DownloadAsync(
        string engagementId,
        string artifactId,
        IEngagementRepository engagements,
        IPayloadStore payloads,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(artifactId, out var artifactValue))
            return Results.BadRequest(new Problem("Artifact id is not a valid identifier."));

        var engagement = await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken);
        if (engagement is null)
            return Results.NotFound(new Problem("Engagement does not exist."));

        var payload = await payloads.FindAsync(artifactValue, engagementValue, cancellationToken);
        if (payload is null)
            return Results.NotFound(new Problem("Payload does not exist in this engagement."));

        var fileName = $"rod-{payload.Class.ToLowerInvariant()}-{payload.PayloadId.ToString("N")[..8]}.bin";
        return Results.File(payload.Content, payload.ContentType, fileName);
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    // The malleable transport knobs are all optional: EnrollPath,
    // UserAgent, Headers, RequestTimeoutSeconds, Envelope, FallbackEndpoints.
    // An operator who omits them gets a profile with the unchanged wire shape.
    // Defaulted so a minimal positional construction (as in the integration
    // tests) stays valid. ListenerId names the engagement's own listener and
    // supplies the endpoint from its record, so the two are mutually
    // exclusive on the wire.
    public sealed record BuildPayloadRequest(
        string? Language,
        string? Class,
        string? TargetOs,
        string? TargetArch,
        string? Endpoint,
        string? UriPath,
        double? SleepSeconds,
        double? JitterSeconds,
        DateTimeOffset? KillDate,
        string? ListenerId = null,
        string? Mode = null,
        string? EnrollPath = null,
        string? UserAgent = null,
        Dictionary<string, string>? Headers = null,
        double? RequestTimeoutSeconds = null,
        string? Envelope = null,
        string? Stage2PayloadId = null,
        List<string>? FallbackEndpoints = null,
        int? TokenMaxUses = null,
        long? TokenLifetimeSeconds = null);

    // The response's TokenId names the enrollment credential baked into the
    // artifact (null on a credential-free build): enough to revoke it, never
    // enough to reuse it -- the secret itself exists only inside the artifact.
    public sealed record BuildPayloadResponse(
        string ArtifactId,
        string EngagementId,
        string Class,
        string Language,
        string ContentType,
        long Size,
        string Fingerprint,
        DateTimeOffset BuiltAt,
        string[]? Transforms = null,
        string? TokenId = null);

    /// <summary>
    /// One row of the payload library: a stored payload's metadata without the
    /// bytes. The engagement is the path, not the row. <see cref="Target"/> and
    /// <see cref="Endpoint"/> are null on payloads built before those fields
    /// were recorded.
    /// </summary>
    public sealed record PayloadSummaryResponse(
        string ArtifactId,
        string Class,
        string Language,
        string? Target,
        string? Endpoint,
        string ContentType,
        long Size,
        string Fingerprint,
        DateTimeOffset BuiltAt,
        string? TokenId = null)
    {
        public static PayloadSummaryResponse Of(Rod.Audit.PayloadRecord record) => new(
            record.PayloadId.ToString(),
            record.Class,
            record.Language,
            record.Target,
            record.Endpoint,
            record.ContentType,
            record.Size,
            record.Fingerprint,
            record.BuiltAt,
            TokenId: record.TokenId?.ToString());
    }

    public sealed record Problem(string Error);
}
