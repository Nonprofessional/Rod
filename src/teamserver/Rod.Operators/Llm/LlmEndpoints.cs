using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
using Rod.Operators.Endpoints;
// TaskStatus is ambiguous against System.Threading.Tasks.TaskStatus under
// implicit usings; pin it to the task entity's lifecycle enum.
using TaskStatus = Rod.CoreState.Tasks.TaskStatus;

namespace Rod.Operators.Llm;

/// <summary>
/// The LLM summarize endpoint (architecture.md Sec 11, the
/// attention-compression surface): <c>POST /engagements/{id}/tasks/{taskId}:summarize</c>
/// sends one completed task's captured output to the configured
/// OpenAI-compatible endpoint and returns the summary. Read-scope gated like
/// every read of the engagement's state -- the request egresses content, and
/// that egress is itself the act the trail records: every attempt, succeeded
/// or failed, lands as an <see cref="AuditEventKind.LlmSummaryGenerated"/>
/// event attributed to the requesting operator and bound to the task. When
/// the integration is disabled the route answers 503 naming the
/// configuration section -- opt-in is the standing default, and the runbook
/// documents the egress decision.
/// </summary>
public static class LlmEndpoints
{
    public static IEndpointRouteBuilder MapLlmEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/engagements/{engagementId}/tasks/{taskId}:summarize", SummarizeAsync)
            .RequireAuthorization().AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Read))
            .WithName("SummarizeTask");
        return endpoints;
    }

    private static async Task<IResult> SummarizeAsync(
        string engagementId,
        string taskId,
        ClaimsPrincipal user,
        IEngagementRepository engagements,
        ITaskRepository tasks,
        LlmSummarizer llm,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var requestedBy = user.TryGetOperatorId();
        if (requestedBy is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(taskId, out var taskValue))
            return Results.BadRequest(new Problem("Task id is not a valid identifier."));

        var engagement = await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken);
        if (engagement is null)
            return Results.NotFound(new Problem("No such engagement."));

        var task = await tasks.FindAsync(new TaskId(taskValue), cancellationToken);
        if (task is null || task.EngagementId != engagement.Id)
            return Results.NotFound(new Problem("No such task in this engagement."));
        if (task.Status != TaskStatus.Completed)
            return Results.Json(
                new Problem("Only a completed task carries the final output a summary reads."),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        if (!llm.Configured)
            return Results.Json(
                new Problem("The LLM integration is disabled. Set the Llm configuration section (BaseUrl, ApiKey, Model, Enabled) to enable it; see docs/operations/llm.md."),
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var result = await llm.SummarizeTaskAsync(task, cancellationToken);
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagement.Id.Value,
                operatorId: requestedBy.Value.Value,
                implantId: task.ImplantId.Value,
                taskId: task.Id.Value,
                verb: "llm.summarize",
                kind: AuditEventKind.LlmSummaryGenerated,
                payload: $"summarized {task.Verb} task ({(task.Output ?? string.Empty).Length} chars captured)",
                output: result.Summary,
                outcome: result.Succeeded ? $"succeeded:{result.Model}" : $"failed:{result.Reason}",
                at: clock.GetUtcNow()),
            cancellationToken);

        return result.Succeeded
            ? Results.Ok(new SummarizeTaskResponse(task.Id.ToString(), result.Model, result.Summary!))
            : Results.Json(
                new Problem($"The LLM request failed: {result.Reason}"),
                statusCode: StatusCodes.Status502BadGateway);
    }

    public sealed record SummarizeTaskResponse(string TaskId, string Model, string Summary);
}
