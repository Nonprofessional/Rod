using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Presence;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing implant/session listing (roadmap M1.5, the operator UI):
/// which implants have enrolled into an engagement, with a live online indicator.
/// Joins the implant registry with the presence registry so the operator UI can
/// show sessions at a glance. Scoped by engagement so implant identity never
/// leaks across engagements (architecture.md Sec 3).
/// </summary>
public static class ImplantEndpoints
{
    public static IEndpointRouteBuilder MapImplantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/engagements/{engagementId}/implants");
        group.MapGet("/", ListImplantsAsync).WithName(nameof(ListImplantsAsync));
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

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record ImplantResponse(
        string ImplantId,
        string EngagementId,
        string Class,
        DateTimeOffset KillDate,
        DateTimeOffset CreatedAt,
        bool IsOnline);

    public sealed record Problem(string Error);
}
