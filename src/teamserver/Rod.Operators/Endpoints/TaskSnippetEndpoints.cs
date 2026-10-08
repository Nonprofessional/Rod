using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Snippets;
using Rod.Operators.Snippets;
using Task = System.Threading.Tasks.Task;

namespace Rod.Operators.Endpoints;

/// <summary>
/// The task-snippet surface, engagement-scoped under
/// <c>/engagements/{engagementId}/task-snippets</c>. Plain CRUD: replay
/// happens in the console, which issues each step through the ordinary
/// tasking path, so there is no run endpoint here -- a saved sequence is
/// keyboard shorthand, not a server-side program. Mutations land in the
/// engagement trail through <see cref="TaskSnippetService"/>.
/// </summary>
public static class TaskSnippetEndpoints
{
    public static IEndpointRouteBuilder MapTaskSnippetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/engagements/{engagementId}/task-snippets")
            .RequireAuthorization(OperatorScopes.ReadPolicy);

        group.MapPost(string.Empty, CreateAsync).RequireAuthorization(OperatorScopes.TaskPolicy)
            .WithName("CreateTaskSnippet");
        group.MapGet(string.Empty, ListAsync).WithName("ListTaskSnippets");
        group.MapGet("/{snippetId}", GetAsync).WithName("GetTaskSnippet");
        group.MapDelete("/{snippetId}", DeleteAsync).RequireAuthorization(OperatorScopes.TaskPolicy)
            .WithName("DeleteTaskSnippet");
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        string engagementId,
        CreateTaskSnippetRequest body,
        ClaimsPrincipal user,
        TaskSnippetService service,
        IEngagementRepository engagements,
        CancellationToken cancellationToken)
    {
        var actor = user.TryGetOperatorId();
        if (actor is null)
            return Results.Unauthorized();
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        // An unknown engagement is a routing problem (404); everything the
        // service refuses after that is a well-formed request the server
        // declines (422), the same split the task endpoints hold.
        if (await engagements.FindAsync(engagement, cancellationToken) is null)
            return Results.NotFound(new Problem("Unknown engagement."));
        if (body is null || string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new Problem("A snippet needs a name."));
        if (body.Steps is not { Count: > 0 })
            return Results.BadRequest(new Problem("A snippet needs at least one step."));

        try
        {
            var snippet = await service.CreateAsync(
                new TaskSnippetService.CreateSnippetCommand(
                    engagement,
                    body.Name,
                    body.Steps.Select(s => new TaskSnippetStep(s.Verb ?? string.Empty, s.Arguments ?? string.Empty)).ToArray(),
                    actor.Value),
                cancellationToken);
            return Results.Created(
                $"/engagements/{engagement}/task-snippets/{snippet.Id}",
                TaskSnippetResponse.Of(snippet));
        }
        catch (TaskSnippetRejectedException ex)
        {
            return Results.Json(new Problem(ex.Message), statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new Problem(ex.Message));
        }
    }

    private static async Task<IResult> ListAsync(
        string engagementId,
        ClaimsPrincipal user,
        TaskSnippetService service,
        CancellationToken cancellationToken)
    {
        if (user.TryGetOperatorId() is null)
            return Results.Unauthorized();
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var snippets = await service.ListAsync(engagement, cancellationToken);
        return Results.Ok(new TaskSnippetListResponse(snippets.Select(TaskSnippetResponse.Of).ToArray()));
    }

    private static async Task<IResult> GetAsync(
        string engagementId,
        string snippetId,
        ClaimsPrincipal user,
        TaskSnippetService service,
        CancellationToken cancellationToken)
    {
        if (user.TryGetOperatorId() is null)
            return Results.Unauthorized();
        if (!TryResolve(engagementId, snippetId, out var engagement, out var snippet, out var error))
            return error!;

        var stored = await service.FindAsync(engagement, snippet, cancellationToken);
        return stored is null
            ? Results.NotFound(new Problem("The task snippet does not exist in this engagement."))
            : Results.Ok(TaskSnippetResponse.Of(stored));
    }

    private static async Task<IResult> DeleteAsync(
        string engagementId,
        string snippetId,
        ClaimsPrincipal user,
        TaskSnippetService service,
        CancellationToken cancellationToken)
    {
        var actor = user.TryGetOperatorId();
        if (actor is null)
            return Results.Unauthorized();
        if (!TryResolve(engagementId, snippetId, out var engagement, out var snippet, out var error))
            return error!;

        var deleted = await service.DeleteAsync(engagement, snippet, actor.Value, cancellationToken);
        return deleted
            ? Results.NoContent()
            : Results.NotFound(new Problem("The task snippet does not exist in this engagement."));
    }

    private static bool TryResolve(
        string engagementId,
        string snippetId,
        out EngagementId engagement,
        out TaskSnippetId snippet,
        out IResult? error)
    {
        if (!EngagementId.TryParse(engagementId, out engagement)
            || !TaskSnippetId.TryParse(snippetId, out snippet))
        {
            snippet = default;
            error = Results.BadRequest(new Problem("Identifier is not a valid id."));
            return false;
        }

        error = null;
        return true;
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record TaskSnippetStepRequest(string? Verb, string? Arguments);

    public sealed record CreateTaskSnippetRequest(string? Name, IReadOnlyList<TaskSnippetStepRequest>? Steps);

    public sealed record TaskSnippetStepResponse(string Verb, string Arguments);

    public sealed record TaskSnippetResponse(
        string Id,
        string EngagementId,
        string Name,
        IReadOnlyList<TaskSnippetStepResponse> Steps,
        DateTimeOffset CreatedAt,
        string CreatedBy)
    {
        public static TaskSnippetResponse Of(TaskSnippet snippet) => new(
            snippet.Id.ToString(),
            snippet.EngagementId.ToString(),
            snippet.Name,
            snippet.Steps.Select(s => new TaskSnippetStepResponse(s.Verb, s.Arguments)).ToArray(),
            snippet.CreatedAt,
            snippet.CreatedBy.ToString());
    }

    public sealed record TaskSnippetListResponse(IReadOnlyList<TaskSnippetResponse> Snippets);
}
