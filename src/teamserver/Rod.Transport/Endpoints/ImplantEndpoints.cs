using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing implant endpoints (, the operator UI):
/// which implants have enrolled into an engagement, with a live online indicator
/// and their retirement state, plus the tasks directed at a given implant and
/// the retire action. Joins the implant registry with the session registry so
/// the operator UI can show sessions at a glance, and reads the task registry
/// for an implant's task history. Scoped by engagement so implant identity never
/// leaks across engagements (architecture.md Sec 3).
///
/// An implant is online exactly when it has an active session (); the
/// listing projects that onto the enrolled implants. Retiring an implant
/// () takes it out of operation: it is marked retired, its active
/// session is closed, and the retire is recorded in the engagement audit trail.
/// </summary>
public static class ImplantEndpoints
{
    public static IEndpointRouteBuilder MapImplantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Operator-facing: implant views and retire require an authenticated
        // operator session.
        var group = endpoints.MapGroup("/engagements/{engagementId}/implants").RequireAuthorization();
        group.MapGet("/", ListImplantsAsync).WithName(nameof(ListImplantsAsync));
        group.MapGet("/{implantId}/tasks", ListImplantTasksAsync)
            .WithName(nameof(ListImplantTasksAsync));
        group.MapPost("/{implantId}:retire", RetireAsync).WithName(nameof(RetireAsync));
        return endpoints;
    }

    private static async Task<IResult> ListImplantsAsync(
        string engagementId,
        IImplantRepository implants,
        ISessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var engagementKey = new EngagementId(engagementValue);
        var enrolled = await implants.ListByEngagementAsync(engagementKey, cancellationToken);
        var online = await sessions.ListActiveAsync(engagementKey, cancellationToken);
        var onlineById = online.Select(s => s.ImplantId).ToHashSet();

        var body = enrolled
            .Select(i => new ImplantResponse(
                i.Id.ToString(),
                i.EngagementId.ToString(),
                i.Class.ToString(),
                i.KillDate,
                i.CreatedAt,
                IsOnline: onlineById.Contains(i.Id),
                i.RetiredAt,
                ParentImplantId: i.ParentImplantId?.ToString()))
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

    private static async Task<IResult> RetireAsync(
        string engagementId,
        string implantId,
        ClaimsPrincipal user,
        ImplantService service,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        // The retiring operator is the authenticated operator, resolved off the
        // session principal rather than named in the body (operator auth).
        var retiredBy = user.TryGetOperatorId();
        if (retiredBy is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(implantId, out var implantValue))
            return Results.BadRequest(new Problem("Implant id is not a valid identifier."));

        Rod.CoreState.Application.ImplantRetired retired;
        try
        {
            retired = await service.RetireAsync(
                new RetireImplantCommand(
                    new EngagementId(engagementValue),
                    new ImplantId(implantValue),
                    retiredBy.Value),
                cancellationToken);
        }
        catch (ImplantNotFoundException ex)
        {
            // Unknown or foreign implant -- same shape the listing and task
            // endpoints return for a foreign implant (architecture.md Sec 3).
            return Results.NotFound(new Problem(ex.Message));
        }

        // The retire is recorded (architecture.md Sec 11): an ImplantRetired
        // audit event in the engagement trail, attributed to the retiring
        // operator. The outcome is the recorded retirement timestamp. No task is
        // involved -- retirement is an operator action on the implant. The store
        // stamps the chain hashes on append; the call site supplies only the
        // facts. Idempotent: a duplicate retire is still recorded, so the trail
        // reflects every operator action; JustRetired distinguishes the first.
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: retired.EngagementId.Value,
                operatorId: retired.RetiredBy.Value,
                implantId: retired.ImplantId.Value,
                taskId: Guid.Empty,
                verb: "retire",
                kind: AuditEventKind.ImplantRetired,
                payload: retired.JustRetired ? "retired" : "already retired",
                output: null,
                outcome: retired.RetiredAt.ToString("O"),
                at: retired.RetiredAt),
            cancellationToken);

        return Results.Ok(new RetireImplantResponse(
            retired.ImplantId.ToString(),
            retired.EngagementId.ToString(),
            retired.RetiredBy.ToString(),
            retired.RetiredAt,
            retired.JustRetired,
            retired.ClosedSession?.ToString()));
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record ImplantResponse(
        string ImplantId,
        string EngagementId,
        string Class,
        DateTimeOffset KillDate,
        DateTimeOffset CreatedAt,
        bool IsOnline,
        DateTimeOffset? RetiredAt,
        string? ParentImplantId = null);

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

    public sealed record RetireImplantResponse(
        string ImplantId,
        string EngagementId,
        string RetiredBy,
        DateTimeOffset RetiredAt,
        bool JustRetired,
        string? ClosedSession);

    public sealed record Problem(string Error);
}
