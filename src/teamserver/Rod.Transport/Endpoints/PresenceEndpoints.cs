using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Sessions;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing presence query: which implants are online
/// in an engagement. Lets an operator observe
/// that a connecting implant appeared in its engagement -- the acceptance
/// point -- and is scoped by engagement so presence never leaks across
/// engagements (architecture.md Sec 3).
///
/// Presence is the active-sessions projection: an implant is
/// online exactly when it has an Active session. Backed by
/// <see cref="ISessionRegistry"/>; the response carries the session id alongside
/// the implant id.
/// </summary>
public static class PresenceEndpoints
{
    public static IEndpointRouteBuilder MapPresenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Operator-facing: presence reads require an authenticated operator session.
        var group = endpoints
            .MapGroup("/engagements/{engagementId}/presence")
            .RequireAuthorization();

        group.MapGet("/", ListOnlineAsync).WithName(nameof(ListOnlineAsync));

        return endpoints;
    }

    private static async Task<IResult> ListOnlineAsync(
        string engagementId,
        ISessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var idValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var online = await sessions.ListActiveAsync(new EngagementId(idValue), cancellationToken);
        var body = online.Select(SessionResponse.Of).ToArray();

        return Results.Ok(body);
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record PresenceRecordResponse(
        string SessionId,
        string ImplantId,
        string EngagementId,
        string[] Capabilities,
        DateTimeOffset OnlineAt,
        DateTimeOffset LastSeenAt);

    private static class SessionResponse
    {
        public static PresenceRecordResponse Of(Session s)
            => new(
                s.Id.ToString(),
                s.ImplantId.ToString(),
                s.EngagementId.ToString(),
                s.Capabilities.ToArray(),
                OnlineAt: s.StartedAt,
                LastSeenAt: s.LastSeenAt);
    }

}
