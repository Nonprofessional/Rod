using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing operational-event-log read endpoint (): the
/// per-engagement, append-only, attributed event stream (architecture.md Sec 11).
/// Every action that changes engagement state or binds an identity produces an
/// immutable, hash-chained event; this endpoint returns that trail oldest-first
/// so the engagement timeline is observable end to end. It is the raw evidence
/// feed -- timeline and report export () are later consumers of the same
/// store. Distinct from the operators-layer <c>GET .../events</c> SSE route,
/// which is the transient live fan-out, not the durable trail.
/// </summary>
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Operator-facing: the event trail requires an authenticated operator session.
        var group = endpoints
            .MapGroup("/engagements/{engagementId}/audit")
            .RequireAuthorization();
        group.MapGet("/", ListAsync).WithName(nameof(ListAsync));
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string engagementId,
        int? limit,
        string? cursor,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!ListPaging.TryBind(limit, cursor, c => TimestampIdCursor.TryDecode(c, out _, out _),
                out var boundLimit, out var boundCursor, out var pagingError))
        {
            return Results.BadRequest(new Problem(pagingError));
        }

        // The trail is per-engagement by construction (architecture.md Sec 3/11);
        // cross-engagement access never reaches here with another engagement's id.
        // One page per request: newest window first across pages, oldest first
        // within a page so the page itself still reads in causal order.
        var page = await audit.ListPageAsync(engagementValue, boundLimit, boundCursor, cancellationToken);
        var body = new AuditListResponse(
            page.Items.Select(AuditEventEntry.Of).ToArray(),
            page.NextCursor);
        return Results.Ok(body);
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    /// <summary>
    /// A single attributed event on the engagement trail. Carries the full
    /// attribution surface (operator/implant/task ids) so the timeline shows who
    /// did what against which entity, plus the kind/verb/payload/outcome that
    /// describe the action. The hash-chain fields are not surfaced here: the trail
    /// is tamper-evident by construction and a report consumer reads the facts,
    /// not the chain internals.
    /// </summary>
    public sealed record AuditEventEntry(
        Guid EventId,
        string Kind,
        string Verb,
        Guid OperatorId,
        Guid ImplantId,
        Guid TaskId,
        string Payload,
        string? Output,
        string Outcome,
        DateTimeOffset At)
    {
        public static AuditEventEntry Of(AuditEvent e)
            => new(
                e.EventId,
                e.Kind.ToString(),
                e.Verb,
                e.OperatorId,
                e.ImplantId,
                e.TaskId,
                e.Payload,
                e.Output,
                e.Outcome,
                e.At);
    }

    /// <summary>
    /// One page of the engagement's audit trail: the page's events (oldest first
    /// within the page) plus the cursor that walks one page older, null when the
    /// beginning of the trail is reached.
    /// </summary>
    public sealed record AuditListResponse(
        AuditEventEntry[] Items,
        string? NextCursor);

    public sealed record Problem(string Error);
}
