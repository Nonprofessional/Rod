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
/// The operator-facing artifact endpoints: artifacts -- files,
/// screenshots, captured command output -- are first-class evidence objects
/// linked to the task that gathered them, not loose files (architecture.md Sec 11).
/// Lets an operator attach an artifact to a task, list a task's artifacts, and
/// retrieve one back -- the acceptance point. The evidence and the tasking
/// that gathered it stay bound, so the report consumers read artifacts
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
            .RequireAuthorization().AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Read));
        taskGroup.MapPost("/", AttachArtifactAsync).AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName(nameof(AttachArtifactAsync));
        taskGroup.MapGet("/", ListArtifactsAsync).WithName(nameof(ListArtifactsAsync));

        var engagementGroup = endpoints
            .MapGroup("/engagements/{engagementId}/artifacts")
            .RequireAuthorization().AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Read));
        engagementGroup.MapGet("/{artifactId}", GetArtifactAsync).WithName(nameof(GetArtifactAsync));

        // The typed loot view (architecture.md Sec 11.2): the engagement-wide
        // artifact listing classified by what gathered each artifact -- the
        // organizer over evidence the collection verbs already captured.
        endpoints.MapGet("/engagements/{engagementId}/loot", ListLootAsync)
            .RequireAuthorization().AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Read))
            .WithName(nameof(ListLootAsync));

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

        // The attachment is recorded (architecture.md Sec 11): an
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
        int? limit,
        string? cursor,
        IArtifactStore artifacts,
        ITaskRepository tasks,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(taskId, out var taskValue))
            return Results.BadRequest(new Problem("Task id is not a valid identifier."));
        if (!ListPaging.TryBind(limit, cursor, c => TimestampIdCursor.TryDecode(c, out _, out _),
                out var boundLimit, out var boundCursor, out var pagingError))
        {
            return Results.BadRequest(new Problem(pagingError));
        }

        var task = await tasks.FindAsync(new TaskId(taskValue), cancellationToken);
        if (task is null || task.EngagementId != new EngagementId(engagementValue))
            return Results.NotFound(new Problem("Task does not exist in this engagement."));

        // One page of the task's artifacts, newest window first across pages --
        // a heavily evidenced task no longer grows this listing without bound.
        var page = await artifacts.ForTaskPageAsync(taskValue, boundLimit, boundCursor, cancellationToken);
        var body = new ArtifactListResponse(
            page.Items.Select(ArtifactResponse.Of).ToArray(),
            page.NextCursor);
        return Results.Ok(body);
    }

    private static async Task<IResult> GetArtifactAsync(
        string engagementId,
        string artifactId,
        ClaimsPrincipal user,
        IArtifactStore artifacts,
        ITaskRepository tasks,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(artifactId, out var artifactValue))
            return Results.BadRequest(new Problem("Artifact id is not a valid identifier."));

        var artifact = await artifacts.FindAsync(artifactValue, cancellationToken);
        if (artifact is null || artifact.EngagementId != engagementValue)
            return Results.NotFound(new Problem("Artifact does not exist in this engagement."));

        // Retrieving evidence bytes is an act on the engagement, unlike
        // reading a projection of it (architecture.md Sec 11.2): the artifact
        // leaves the platform, so the chain-of-custody question "who pulled
        // what" gets the same trail record the payload fetch route writes for
        // delivered bytes. The task's implant binds the event to the target
        // the evidence came from; a task that no longer resolves (or an
        // artifact that never had one) leaves the id unused rather than
        // blocking the read.
        var implantId = Guid.Empty;
        var auditTaskId = Guid.Empty;
        if (artifact.TaskId is { } taskKey)
        {
            var task = await tasks.FindAsync(new TaskId(taskKey), cancellationToken);
            if (task is not null)
                implantId = task.ImplantId.Value;
            auditTaskId = taskKey;
        }
        var viewer = user.TryGetOperatorId();
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagementValue,
                operatorId: viewer is { } who ? who.Value : Guid.Empty,
                implantId: implantId,
                taskId: auditTaskId,
                verb: "view-artifact",
                kind: AuditEventKind.ArtifactViewed,
                payload: $"{artifact.Name};{artifact.ContentType}",
                output: null,
                outcome: artifact.ArtifactId.ToString("N"),
                at: clock.GetUtcNow()),
            cancellationToken);

        return Results.File(artifact.Content, artifact.ContentType, artifact.Name);
    }

    // The loot kinds: what gathered the artifact, the read-time judgment the
    // loot view classifies by. The verbs are the collection-family names the
    // capability catalog carries; transport cannot reference the tradecraft
    // layer that owns them (the layer rule), so the strings stand here with
    // the catalog as their authority.
    internal const string LootKindScreenshot = "screenshot";
    internal const string LootKindCredential = "credential";
    internal const string LootKindFile = "file";
    internal const string LootKindOther = "other";

    private static readonly IReadOnlySet<string> LootKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        LootKindScreenshot, LootKindCredential, LootKindFile, LootKindOther,
    };

    private static async Task<IResult> ListLootAsync(
        string engagementId,
        string? kind,
        int? limit,
        string? cursor,
        IEngagementRepository engagements,
        IArtifactStore artifacts,
        ITaskRepository tasks,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (kind is not null && !LootKinds.Contains(kind))
            return Results.BadRequest(new Problem($"kind must be one of: {string.Join(", ", LootKinds)}."));
        if (!ListPaging.TryBind(limit, cursor, c => TimestampIdCursor.TryDecode(c, out _, out _),
                out var boundLimit, out var boundCursor, out var pagingError))
        {
            return Results.BadRequest(new Problem(pagingError));
        }

        var engagement = await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken);
        if (engagement is null)
            return Results.NotFound(new Problem("Engagement does not exist."));

        // One page of the engagement's artifacts, newest window first, then
        // the classification join: the producing task names the verb, and the
        // verb plus content type name the kind. The kind filter applies to
        // the classified page, so a filtered walk may hold fewer items per
        // page -- the cursor still walks strictly older, the paging
        // contract's own guarantee.
        var page = await artifacts.ListPageAsync(engagementValue, boundLimit, boundCursor, cancellationToken);
        var taskCache = new Dictionary<Guid, Rod.CoreState.Tasks.Task?>();
        var items = new List<LootEntry>();
        foreach (var artifact in page.Items)
        {
            // A task-less artifact (the recon workbench's pre-foothold
            // findings) classifies by content type alone -- the verb join has
            // nothing to read, and the entry carries its operator attribution.
            Rod.CoreState.Tasks.Task? task = null;
            if (artifact.TaskId is { } taskKey)
            {
                if (!taskCache.TryGetValue(taskKey, out task))
                {
                    task = await tasks.FindAsync(new TaskId(taskKey), cancellationToken);
                    taskCache[taskKey] = task;
                }
            }

            var lootKind = ClassifyLoot(task?.Verb, artifact.ContentType);
            if (kind is not null && !string.Equals(lootKind, kind, StringComparison.OrdinalIgnoreCase))
                continue;

            items.Add(new LootEntry(
                artifact.ArtifactId.ToString("N"),
                lootKind,
                artifact.TaskId?.ToString("N"),
                task?.ImplantId.ToString(),
                task?.Verb,
                artifact.OperatorId,
                artifact.Name,
                artifact.ContentType,
                artifact.Size,
                artifact.StoredAt));
        }

        return Results.Ok(new LootListResponse([.. items], page.NextCursor));
    }

    // The classification: the producing verb names the intent, the content
    // type catches what a verb-less record still declares (an operator
    // attaching a PNG needs no task verb to be a screenshot in the view).
    // Anything else is still loot -- "other" keeps the view complete rather
    // than silently dropping what the vocabulary has not met.
    private static string ClassifyLoot(string? verb, string contentType)
    {
        if (string.Equals(verb, "collect.screenshot", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return LootKindScreenshot;
        }

        if (string.Equals(verb, "collect.cred", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "collect.minidump", StringComparison.OrdinalIgnoreCase))
        {
            return LootKindCredential;
        }

        if (string.Equals(verb, "file.pull", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "file.push", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "exfil.push", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "exfil.stage", StringComparison.OrdinalIgnoreCase))
        {
            return LootKindFile;
        }

        return LootKindOther;
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

    /// <summary>
    /// One page of a task's artifacts: the page's records plus the cursor that
    /// walks one page older, null when the beginning is reached.
    /// </summary>
    public sealed record ArtifactListResponse(
        ArtifactResponse[] Items,
        string? NextCursor);

    // The list shape omits the artifact bytes -- an artifact's metadata is small
    // and enumerable, its content is fetched on demand through the retrieve
    // endpoint. Mirrors how TaskResponse carries the task but not its result blob.
    // The task id is nullable: pre-foothold findings carry no task.
    public sealed record ArtifactResponse(
        string ArtifactId,
        string? TaskId,
        Guid? OperatorId,
        string Name,
        string ContentType,
        long Size,
        DateTimeOffset StoredAt)
    {
        public static ArtifactResponse Of(Artifact artifact)
            => new(
                artifact.ArtifactId.ToString("N"),
                artifact.TaskId?.ToString("N"),
                artifact.OperatorId,
                artifact.Name,
                artifact.ContentType,
                artifact.Size,
                artifact.StoredAt);
    }

    /// <summary>
    /// One page of the engagement's typed loot (architecture.md Sec 11.2): the
    /// classified artifact records plus the cursor that walks one page older,
    /// null when the beginning is reached. A kind-filtered page may hold fewer
    /// items than the limit -- the filter applies to the classified page, not
    /// the store walk.
    /// </summary>
    public sealed record LootListResponse(
        LootEntry[] Items,
        string? NextCursor);

    // One piece of loot: the artifact's metadata, the kind the view classifies
    // it into, and the capture attribution -- which task (and verb) gathered
    // it, from which implant, credited to which operator. A pre-foothold
    // finding carries no task: the task, implant, and verb are null and the
    // operator attribution is the whole story. The bytes are fetched
    /// on demand through the retrieve endpoint; the retrieval is what the
    /// ArtifactViewed event records.
    public sealed record LootEntry(
        string ArtifactId,
        string Kind,
        string? TaskId,
        string? ImplantId,
        string? Verb,
        Guid? CapturedBy,
        string Name,
        string ContentType,
        long Size,
        DateTimeOffset StoredAt);

}
