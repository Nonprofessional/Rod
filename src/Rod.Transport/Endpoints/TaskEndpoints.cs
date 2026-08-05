using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Tasks;
// The domain entity shares its name with System.Threading.Tasks.Task. Pin it
// here so the endpoint's Task references resolve to the entity; the BCL type is
// not used by name in this file (handlers return IResult).
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing tasking endpoints (roadmap M1.4): issue a task against an
/// implant in an engagement, and read a task back with its captured result and
/// audit trail. Lets an operator task an implant and see output plus an audit
/// event -- the M1.4 acceptance point. Scoped by engagement so tasking never
/// crosses engagement boundaries (architecture.md Sec 3).
/// </summary>
public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/engagements/{engagementId}/tasks");

        group.MapPost("/", IssueAsync).WithName("IssueTask");
        group.MapGet("/{taskId}", GetAsync).WithName("GetTask");

        return endpoints;
    }

    private static async Task<IResult> IssueAsync(
        string engagementId,
        IssueTaskRequest body,
        TaskService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(body.ImplantId, out var implantValue))
            return Results.BadRequest(new Problem("Implant id is not a valid identifier."));
        if (body.IssuedBy is null)
            return Results.BadRequest(new Problem("Issuing operator id is required."));
        if (string.IsNullOrWhiteSpace(body.Verb))
            return Results.BadRequest(new Problem("Task verb is required."));

        TaskIssued issued;
        try
        {
            issued = await service.IssueAsync(
                new IssueTaskCommand(
                    new EngagementId(engagementValue),
                    new ImplantId(implantValue),
                    new OperatorId(body.IssuedBy.Value),
                    body.Verb,
                    body.Arguments ?? string.Empty),
                cancellationToken);
        }
        catch (TaskRejectedException ex)
        {
            // An unsupported verb and a retired implant are both well-formed
            // requests the server refuses to act on -> 422; an unknown or
            // foreign implant is a routing failure -> 404.
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

        return Results.Created($"/engagements/{response.EngagementId}/tasks/{response.TaskId}", response);
    }

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

    public sealed record IssueTaskRequest(
        string ImplantId,
        Guid? IssuedBy,
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
