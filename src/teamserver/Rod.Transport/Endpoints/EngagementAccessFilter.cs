using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;

namespace Rod.Transport.Endpoints;

/// <summary>The access tier a route demands: entry (any member) or action (writer and above).</summary>
public enum EngagementAccessRequirement
{
    Read = 0,
    Write = 1,
}

/// <summary>
/// The engagement-scope enforcement for every engagement-scoped route
/// (architecture.md Sec 3): resolve the caller's standing through
/// <see cref="EngagementAccessResolver"/> and hold the route to the tier it
/// names. A stranger and a missing engagement both render 404 -- the
/// engagement's existence is itself information -- and a reader pressing a
/// write route is refused with 403. The filter reads the store fresh per
/// request, so a re-tier or removal takes effect at the very next call. A
/// sibling of the operator layer's own filter; the layer rule keeps
/// Rod.Operators from referencing this one, and the two stay word-for-word.
/// </summary>
public sealed class EngagementAccessFilter(EngagementAccessRequirement requirement) : IEndpointFilter
{
    // The resolved standing is cached on the request: the group's read filter
    // and a route's write filter share one resolution instead of doubling the
    // store lookups.
    private const string StandingItemKey = "rod-engagement-access";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var operatorId = http.User.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        var raw = http.Request.RouteValues["engagementId"]?.ToString();
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        if (http.Items[StandingItemKey] is not EngagementAccess standing)
        {
            var resolver = http.RequestServices.GetRequiredService<EngagementAccessResolver>();
            standing = await resolver.ResolveAsync(
                new EngagementId(engagementValue), operatorId.Value, http.RequestAborted);
            http.Items[StandingItemKey] = standing;
        }

        if (!standing.EngagementExists || standing.Level == EngagementAccessLevel.None)
            return Results.NotFound(new Problem($"Engagement {raw} does not exist."));
        if (requirement == EngagementAccessRequirement.Write && !standing.CanWrite)
            return Results.Forbid();

        return await next(context);
    }
}
