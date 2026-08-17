using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
// The domain entity shares its name with System.Threading.Tasks.Task. Pin it
// here so the endpoint's Task references resolve to the entity; the BCL type is
// not used by name in this file (handlers return IResult).
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing tasking endpoints: issue a task against an
/// implant in an engagement, read a task back with its captured result and audit
/// trail, and list every task in an engagement ( -- the operator UI
/// shows the engagement's whole task history, not one implant's). Lets an
/// operator task an implant and see output plus an audit event -- the
/// acceptance point -- and survey all tasking across the engagement. Scoped by
/// engagement so tasking never crosses engagement boundaries (architecture.md
/// Sec 3).
/// </summary>
public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Operator-facing: tasking requires an authenticated operator session.
        var group = endpoints.MapGroup("/engagements/{engagementId}/tasks").RequireAuthorization();

        group.MapPost("/", IssueAsync).WithName("IssueTask");
        // The collection route is listed before {taskId} so the literal "/" does
        // not get captured as a task id; ASP.NET Core route matching prefers the
        // more specific template, and the {taskId} segment requires a non-empty
        // value, so "GET /tasks" (no segment) resolves here and "GET /tasks/{id}"
        // resolves to GetAsync. Ordered this way for readability.
        group.MapGet("/", ListAsync).WithName("ListEngagementTasks");
        group.MapGet("/{taskId}", GetAsync).WithName("GetTask");

        return endpoints;
    }

    // Task arguments ride the wire as one string per task and sit in the queue
    // until dispatch; bound them so a single task cannot pin megabytes and every
    // downstream TaskRequest frame stays inside the gRPC message cap.
    private const int MaxArgumentBytes = 512 * 1024;

    private static async Task<IResult> IssueAsync(
        string engagementId,
        IssueTaskRequest body,
        ClaimsPrincipal user,
        TaskService service,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // The issuing operator is the authenticated operator, resolved off the
        // session principal rather than named in the body (operator auth).
        var issuedBy = user.TryGetOperatorId();
        if (issuedBy is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(body.ImplantId, out var implantValue))
            return Results.BadRequest(new Problem("Implant id is not a valid identifier."));
        if (string.IsNullOrWhiteSpace(body.Verb))
            return Results.BadRequest(new Problem("Task verb is required."));
        if (body.Arguments is { Length: > MaxArgumentBytes })
            return Results.BadRequest(new Problem($"Task arguments exceed {MaxArgumentBytes} bytes."));

        TaskIssued issued;
        try
        {
            issued = await service.IssueAsync(
                new IssueTaskCommand(
                    new EngagementId(engagementValue),
                    new ImplantId(implantValue),
                    issuedBy.Value,
                    body.Verb,
                    body.Arguments ?? string.Empty),
                cancellationToken);
        }
        catch (TaskRejectedException ex)
        {
            // An unsupported verb, a retired implant, and an ROE refusal are
            // all well-formed requests the server refuses to act on -> 422; an
            // unknown or foreign implant is a routing failure -> 404.
            if (ex.Reason == TaskRejectionReason.RoeViolation)
            {
                // The refusal is part of the engagement's story, so it lands in
                // the trail (architecture.md Sec 9, Sec 11): attributed to the
                // issuing operator, the payload the refused verb and arguments,
                // the outcome the violated rule. No task exists to carry ids.
                await audit.AppendAsync(
                    AuditEvent.Fact(
                        eventId: Guid.NewGuid(),
                        engagementId: engagementValue,
                        operatorId: issuedBy.Value.Value,
                        implantId: implantValue,
                        taskId: Guid.Empty,
                        verb: body.Verb,
                        kind: AuditEventKind.TaskRoeRefused,
                        payload: body.Arguments ?? string.Empty,
                        output: null,
                        outcome: ex.Message,
                        at: clock.GetUtcNow()),
                    cancellationToken);
                return Results.Json(
                    new Problem(ex.Message),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            return ex.Reason switch
            {
                TaskRejectionReason.UnsupportedVerbForClass
                or TaskRejectionReason.ImplantRetired
                    => Results.Json(new Problem(ex.Message), statusCode: StatusCodes.Status422UnprocessableEntity),
                _ => Results.NotFound(new Problem(ex.Message)),
            };
        }

        var response = new TaskIssuedResponse(
            issued.TaskId.ToString(),
            issued.EngagementId.ToString(),
            issued.ImplantId.ToString(),
            issued.IssuedBy.ToString(),
            issued.Verb,
            issued.Arguments,
            issued.CreatedAt);

        // The task's issuance is recorded (architecture.md Sec 11):
        // attributed to the issuing operator, the payload the verb and arguments,
        // the outcome the new task id. This is the operator's intent; the
        // TaskDispatched event records the server handing it to the implant and
        // TaskCompleted the result.
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: issued.EngagementId.Value,
                operatorId: issued.IssuedBy.Value,
                implantId: issued.ImplantId.Value,
                taskId: issued.TaskId.Value,
                verb: issued.Verb,
                kind: AuditEventKind.TaskIssued,
                payload: issued.Arguments,
                output: null,
                outcome: issued.TaskId.ToString(),
                at: issued.CreatedAt),
            cancellationToken);

        return Results.Created($"/engagements/{response.EngagementId}/tasks/{response.TaskId}", response);
    }

    private static async Task<IResult> ListAsync(
        string engagementId,
        int? limit,
        string? cursor,
        ITaskRepository tasks,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!ListPaging.TryBind(limit, cursor, c => TaskPageCursor.TryDecode(c, out _),
                out var boundLimit, out var boundCursor, out var pagingError))
        {
            return Results.BadRequest(new Problem(pagingError));
        }

        // One page of the engagement's task history across every implant, newest
        // window first across pages, oldest first within a page. Scoped by
        // engagement by construction (architecture.md Sec 3); the operator UI
        // surveys all tasking across the engagement from this view, walking
        // pages instead of loading the unbounded full history. Reuses
        // ImplantTaskResponse (same field set the per-implant listing returns)
        // so the two task-list shapes read identically to a client.
        var page = await tasks.ListByEngagementPageAsync(
            new EngagementId(engagementValue),
            boundLimit,
            boundCursor,
            cancellationToken);
        var body = new TaskListResponse(
            page.Items.Select(ToResponse).ToArray(),
            page.NextCursor);

        return Results.Ok(body);
    }

    private static ImplantEndpoints.ImplantTaskResponse ToResponse(Task t)
        => new(
            t.Id.ToString(),
            t.ImplantId.ToString(),
            t.IssuedBy.ToString(),
            t.Verb,
            t.Arguments,
            t.Status.ToString(),
            t.Output,
            t.Outcome?.ToString(),
            t.CreatedAt,
            t.CompletedAt);

    private static async Task<IResult> GetAsync(
        string engagementId,
        string taskId,
        TaskService service,
        ITaskRepository tasks,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(taskId, out var taskValue))
            return Results.BadRequest(new Problem("Task id is not a valid identifier."));

        var task = await tasks.FindAsync(new TaskId(taskValue), cancellationToken);
        if (task is null || task.EngagementId != new EngagementId(engagementValue))
            return Results.NotFound(new Problem("Task does not exist in this engagement."));

        var events = await audit.ForTaskAsync(task.Id.Value, cancellationToken);

        return Results.Ok(TaskResponse.Of(task, events));
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    // The issuing operator is the authenticated operator; the request carries
    // only the target implant and the verb to run.
    public sealed record IssueTaskRequest(
        string ImplantId,
        string Verb,
        string? Arguments);

    public sealed record TaskIssuedResponse(
        string TaskId,
        string EngagementId,
        string ImplantId,
        string IssuedBy,
        string Verb,
        string Arguments,
        DateTimeOffset CreatedAt);

    /// <summary>
    /// One page of the engagement's task history: the page's items (oldest first
    /// within the page) plus the cursor that walks one page older, null when the
    /// beginning of the history is reached.
    /// </summary>
    public sealed record TaskListResponse(
        ImplantEndpoints.ImplantTaskResponse[] Items,
        string? NextCursor);

    public sealed record TaskResponse(
        string TaskId,
        string EngagementId,
        string ImplantId,
        string IssuedBy,
        string Verb,
        string Arguments,
        string Status,
        string? Output,
        string? Outcome,
        DateTimeOffset CreatedAt,
        DateTimeOffset? DispatchedAt,
        DateTimeOffset? CompletedAt,
        AuditEventResponse[] Audit)
    {
        public static TaskResponse Of(Task task, IReadOnlyList<AuditEvent> events)
            => new(
                task.Id.ToString(),
                task.EngagementId.ToString(),
                task.ImplantId.ToString(),
                task.IssuedBy.ToString(),
                task.Verb,
                task.Arguments,
                task.Status.ToString(),
                task.Output,
                task.Outcome?.ToString(),
                task.CreatedAt,
                task.DispatchedAt,
                task.CompletedAt,
                events.Select(AuditEventResponse.Of).ToArray());
    }

    public sealed record AuditEventResponse(
        Guid EventId,
        string Kind,
        string Verb,
        string Payload,
        string? Output,
        string Outcome,
        DateTimeOffset At)
    {
        public static AuditEventResponse Of(AuditEvent e)
            => new(
                e.EventId,
                e.Kind.ToString(),
                e.Verb,
                e.Payload,
                e.Output,
                e.Outcome,
                e.At);
    }

    public sealed record Problem(string Error);
}
