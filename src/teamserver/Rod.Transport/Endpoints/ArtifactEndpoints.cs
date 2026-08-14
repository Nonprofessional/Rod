using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing artifact endpoints (roadmap M6.2): artifacts -- files,
/// screenshots, captured command output -- are first-class evidence objects
/// linked to the task that gathered them, not loose files (architecture.md Sec 11).
/// Lets an operator attach an artifact to a task, list a task's artifacts, and
/// retrieve one back -- the M6.2 acceptance point. The evidence and the tasking
/// that gathered it stay bound, so the report consumers (M6.3) read artifacts
/// through the same task scoping as the audit trail.
///
/// Scoped by engagement (architecture.md Sec 3): the engagement id in the path
/// binds every lookup, and a retrieve cross-checks the stored artifact's
/// engagement, so an artifact in one engagement is never reachable from another.
/// Attribution is server-resolved: the attaching operator is the authenticated
/// session principal, and the <see cref="ArtifactAttached"/> audit write is
/// composed in the handler -- the artifact store stays audit-agnostic, the
/// transport layer is where the artifact meets the trail.
/// </summary>
public static class ArtifactEndpoints
{
    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Attach and list are task-scoped (an artifact belongs to the task that
        // gathered it); retrieve is engagement-scoped by artifact id, so a saved
        // artifact is reachable without re-threading the task id. All three are
        // operator-facing and require an authenticated operator session.
        var taskGroup = endpoints
            .MapGroup("/engagements/{engagementId}/tasks/{taskId}/artifacts")
            .RequireAuthorization();
        taskGroup.MapPost("/", AttachArtifactAsync).WithName(nameof(AttachArtifactAsync));
        taskGroup.MapGet("/", ListArtifactsAsync).WithName(nameof(ListArtifactsAsync));

        var engagementGroup = endpoints
            .MapGroup("/engagements/{engagementId}/artifacts")
            .RequireAuthorization();
        engagementGroup.MapGet("/{artifactId}", GetArtifactAsync).WithName(nameof(GetArtifactAsync));

        return endpoints;
    }

    private static async Task<IResult> AttachArtifactAsync(
        string engagementId,
        string taskId,
        AttachArtifactRequest body,
        ClaimsPrincipal user,
        IArtifactStore artifacts,
        ITaskRepository tasks,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        // The attaching operator is the authenticated operator, resolved off the
        // session principal rather than named in the body (operator auth).
        var attachedBy = user.TryGetOperatorId();
        if (attachedBy is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(taskId, out var taskValue))
            return Results.BadRequest(new Problem("Task id is not a valid identifier."));
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new Problem("Artifact name is required."));
        if (body.Name.Length > MaxArtifactNameBytes)
            return Results.BadRequest(new Problem($"Artifact name exceeds {MaxArtifactNameBytes} bytes."));
        if (body.Content is null || body.Content.Length == 0)
            return Results.BadRequest(new Problem("Artifact content is required."));
        if (body.Content.Length > MaxArtifactBytes)
            return Results.Json(new Problem($"Artifact content exceeds {MaxArtifactBytes} bytes."),
                statusCode: StatusCodes.Status413PayloadTooLarge);

        var task = await tasks.FindAsync(new TaskId(taskValue), cancellationToken);
        if (task is null || task.EngagementId != new EngagementId(engagementValue))
            return Results.NotFound(new Problem("Task does not exist in this engagement."));

        var artifactId = Guid.NewGuid();
        var contentType = string.IsNullOrWhiteSpace(body.ContentType) ? "application/octet-stream" : body.ContentType;
        var now = DateTimeOffset.UtcNow;

        var artifact = new Artifact(
            ArtifactId: artifactId,
            EngagementId: engagementValue,
            TaskId: taskValue,
            OperatorId: attachedBy.Value.Value,
            Name: body.Name.Trim(),
            ContentType: contentType,
            Content: body.Content,
            Size: body.Content.Length,
            StoredAt: now);

        await artifacts.SaveAsync(artifact, cancellationToken);

        // The attachment is recorded (architecture.md Sec 11, roadmap M6.2): an
        // ArtifactAttached audit event carrying the name and content type, with
        // the new artifact id as its outcome. Attributed to the attaching
        // operator, bound to the task that gathered the evidence -- the same
        // composition shape as the PayloadBuilt write. The store stamps the chain
        // hashes on append; the call site supplies only the facts.
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagementValue,
                operatorId: attachedBy.Value.Value,
                implantId: task.ImplantId.Value,
                taskId: taskValue,
                verb: "attach-artifact",
                kind: AuditEventKind.ArtifactAttached,
                payload: $"{artifact.Name};{artifact.ContentType}",
                output: null,
                outcome: artifactId.ToString("N"),
                at: now),
            cancellationToken);

        var response = ArtifactResponse.Of(artifact);
        return Results.Created(
            $"/engagements/{engagementId}/artifacts/{artifact.ArtifactId:N}",
            response);
    }

    private static async Task<IResult> ListArtifactsAsync(
        string engagementId,
        string taskId,
        IArtifactStore artifacts,
        ITaskRepository tasks,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(taskId, out var taskValue))
            return Results.BadRequest(new Problem("Task id is not a valid identifier."));

        var task = await tasks.FindAsync(new TaskId(taskValue), cancellationToken);
        if (task is null || task.EngagementId != new EngagementId(engagementValue))
            return Results.NotFound(new Problem("Task does not exist in this engagement."));

        var forTask = await artifacts.ForTaskAsync(taskValue, cancellationToken);
        var body = forTask.Select(ArtifactResponse.Of).ToArray();
        return Results.Ok(body);
    }

    private static async Task<IResult> GetArtifactAsync(
        string engagementId,
        string artifactId,
        IArtifactStore artifacts,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(artifactId, out var artifactValue))
            return Results.BadRequest(new Problem("Artifact id is not a valid identifier."));

        var artifact = await artifacts.FindAsync(artifactValue, cancellationToken);
        if (artifact is null || artifact.EngagementId != engagementValue)
            return Results.NotFound(new Problem("Artifact does not exist in this engagement."));

        return Results.File(artifact.Content, artifact.ContentType, artifact.Name);
    }

    // Attachment bounds: a name longer than this is hostile or a bug, and a
    // single evidence object larger than 64 MiB should move to the exfil stream
    // or an object store rather than one JSON attach request.
    private const int MaxArtifactNameBytes = 256;
    private const int MaxArtifactBytes = 64 * 1024 * 1024;

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    // The attach request is JSON, matching every other mutating operator endpoint:
    // the attaching operator is the authenticated principal (not a body field),
    // and the artifact bytes ride as base64 in Content. A base64 field keeps the
    // request shape uniform with the rest of the API and avoids introducing the
    // first multipart handler; large binaries move to an object store when the
    // backend lands.
    public sealed record AttachArtifactRequest(
        string Name,
        string? ContentType,
        byte[] Content);

    // The list shape omits the artifact bytes -- an artifact's metadata is small
    // and enumerable, its content is fetched on demand through the retrieve
    // endpoint. Mirrors how TaskResponse carries the task but not its result blob.
    public sealed record ArtifactResponse(
        string ArtifactId,
        string TaskId,
        Guid? OperatorId,
        string Name,
        string ContentType,
        long Size,
        DateTimeOffset StoredAt)
    {
        public static ArtifactResponse Of(Artifact artifact)
            => new(
                artifact.ArtifactId.ToString("N"),
                artifact.TaskId.ToString("N"),
                artifact.OperatorId,
                artifact.Name,
                artifact.ContentType,
                artifact.Size,
                artifact.StoredAt);
    }

    public sealed record Problem(string Error);
}
