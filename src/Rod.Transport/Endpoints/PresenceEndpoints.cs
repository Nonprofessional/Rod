using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Presence;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing presence query (roadmap M1.3): which implants are online
/// in an engagement, and is a given implant online. Lets an operator observe
/// that a connecting implant appeared in its engagement -- the M1.3 acceptance
/// point -- and is scoped by engagement so presence never leaks across
/// engagements (architecture.md Sec 3).
/// </summary>
public static class PresenceEndpoints
{
    public static IEndpointRouteBuilder MapPresenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/engagements/{engagementId}/presence");

        group.MapGet("/", ListOnlineAsync).WithName(nameof(ListOnlineAsync));
        group.MapGet("/{implantId}", GetAsync).WithName(nameof(GetAsync));

        return endpoints;
    }

    private static async Task<IResult> ListOnlineAsync(
        string engagementId,
        IPresenceRegistry presence,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var idValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var online = await presence.ListOnlineAsync(new EngagementId(idValue), cancellationToken);
        var body = online.Select(r => new PresenceRecordResponse(
            ImplantId: r.ImplantId.ToString(),
            EngagementId: r.EngagementId.ToString(),
            Capabilities: r.Capabilities.ToArray(),
            OnlineAt: r.OnlineAt,
            LastSeenAt: r.LastSeenAt)).ToArray();

        return Results.Ok(body);
    }

    private static async Task<IResult> GetAsync(
        string engagementId,
        string implantId,
        IPresenceRegistry presence,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(implantId, out var implantValue))
            return Results.BadRequest(new Problem("Implant id is not a valid identifier."));

        var record = await presence.FindAsync(new ImplantId(implantValue), cancellationToken);
        if (record is null || record.EngagementId != new EngagementId(engagementValue))
            return Results.NotFound(new Problem("Implant is not online in this engagement."));

        return Results.Ok(new PresenceRecordResponse(
            ImplantId: record.ImplantId.ToString(),
            EngagementId: record.EngagementId.ToString(),
            Capabilities: record.Capabilities.ToArray(),
            OnlineAt: record.OnlineAt,
            LastSeenAt: record.LastSeenAt));
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record PresenceRecordResponse(
        string ImplantId,
        string EngagementId,
        string[] Capabilities,
        DateTimeOffset OnlineAt,
        DateTimeOffset LastSeenAt);

    public sealed record Problem(string Error);
}
