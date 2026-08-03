using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Presence;
using Rod.CoreState.Tasks;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing implant/session listing (roadmap M1.5, the operator UI):
/// which implants have enrolled into an engagement, with a live online indicator,
/// plus the tasks directed at a given implant. Joins the implant registry with
/// the presence registry so the operator UI can show sessions at a glance, and
/// reads the task registry for an implant's task history. Scoped by engagement so
/// implant identity never leaks across engagements (architecture.md Sec 3).
/// </summary>
public static class ImplantEndpoints
{
    public static IEndpointRouteBuilder MapImplantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/engagements/{engagementId}/implants");
        group.MapGet("/", ListImplantsAsync).WithName(nameof(ListImplantsAsync));
        group.MapGet("/{implantId}/tasks", ListImplantTasksAsync)
            .WithName(nameof(ListImplantTasksAsync));
        return endpoints;
    }

    private static async Task<IResult> ListImplantsAsync(
        string engagementId,
        IImplantRepository implants,
        IPresenceRegistry presence,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var engagementKey = new EngagementId(engagementValue);
        var enrolled = await implants.ListByEngagementAsync(engagementKey, cancellationToken);
        var online = await presence.ListOnlineAsync(engagementKey, cancellationToken);
        var onlineById = online.ToDictionary(r => r.ImplantId);

        var body = enrolled
            .Select(i => new ImplantResponse(
                i.Id.ToString(),
                i.EngagementId.ToString(),
                i.Class.ToString(),
                i.KillDate,
                i.CreatedAt,
                IsOnline: onlineById.ContainsKey(i.Id)))
            .ToArray();

        return Results.Ok(body);
    }

    private static async Task<IResult> ListImplantTasksAsync(
        string engagementId,
        string implantId,
        IImplantRepository implants,
        ITaskRepository tasks,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(implantId, out var implantValue))
            return Results.BadRequest(new Problem("Implant id is not a valid identifier."));

        // Confirm the implant belongs to this engagement before listing its
        // tasks; a foreign implant id yields no rows here (architecture.md Sec 3).
        var implant = await implants.FindAsync(new ImplantId(implantValue), cancellationToken);
        if (implant is null || implant.EngagementId != new EngagementId(engagementValue))
            return Results.NotFound(new Problem("Implant does not exist in this engagement."));

        var list = await tasks.ListByImplantAsync(new ImplantId(implantValue), cancellationToken);
        var body = list
            .Select(t => new ImplantTaskResponse(
                t.Id.ToString(),
                t.ImplantId.ToString(),
                t.IssuedBy.ToString(),
                t.Verb,
                t.Arguments,
                t.Status.ToString(),
                t.Output,
                t.Outcome?.ToString(),
                t.CreatedAt,
                t.CompletedAt))
            .ToArray();

        return Results.Ok(body);
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record ImplantResponse(
        string ImplantId,
        string EngagementId,
        string Class,
        DateTimeOffset KillDate,
        DateTimeOffset CreatedAt,
        bool IsOnline);

    public sealed record ImplantTaskResponse(
        string TaskId,
        string ImplantId,
        string IssuedBy,
        string Verb,
        string Arguments,
        string Status,
        string? Output,
        string? Outcome,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt);

    public sealed record Problem(string Error);
}
