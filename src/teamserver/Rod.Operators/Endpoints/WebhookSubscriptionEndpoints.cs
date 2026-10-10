using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Webhooks;
using Rod.Operators.Webhooks;
using Task = System.Threading.Tasks.Task;

namespace Rod.Operators.Endpoints;

/// <summary>
/// The webhook-subscription surface (architecture.md Sec 4.4),
/// engagement-scoped under
/// <c>/engagements/{engagementId}/webhook-subscriptions</c>. The routes
/// live in the operator layer with the SSE stream -- transport may not
/// reference the forwarder's layer, and channels are the forwarder's
/// domain -- and every mutation lands in the engagement trail through
/// <see cref="WebhookService"/>.
/// </summary>
public static class WebhookSubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapWebhookSubscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/engagements/{engagementId}/webhook-subscriptions")
            .RequireAuthorization().AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Read));

        group.MapPost(string.Empty, RegisterAsync).AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("RegisterWebhookSubscription");
        group.MapGet(string.Empty, ListAsync).WithName("ListWebhookSubscriptions");
        group.MapGet("/{subscriptionId}", GetAsync).WithName("GetWebhookSubscription");
        group.MapPost("/{subscriptionId}:enable", EnableAsync).AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("EnableWebhookSubscription");
        group.MapPost("/{subscriptionId}:disable", DisableAsync).AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("DisableWebhookSubscription");
        group.MapPost("/{subscriptionId}:test", TestAsync).AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("TestWebhookSubscription");
        group.MapDelete("/{subscriptionId}", DeleteAsync).AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("DeleteWebhookSubscription");
        return endpoints;
    }

    private static async Task<IResult> RegisterAsync(
        string engagementId,
        CreateWebhookSubscriptionRequest body,
        ClaimsPrincipal user,
        WebhookService service,
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
        // declines (422), the same split the automation rules hold.
        if (await engagements.FindAsync(engagement, cancellationToken) is null)
            return Results.NotFound(new Problem("Unknown engagement."));
        if (body is null)
            return Results.BadRequest(new Problem("A webhook subscription needs a name, a URL, and event kinds."));

        if (!TryResolveKinds(body.EventKinds, out var kinds, out var kindsError))
            return Results.BadRequest(new Problem(kindsError));

        try
        {
            var subscription = await service.RegisterAsync(
                new WebhookService.RegisterCommand(
                    engagement,
                    body.Name ?? string.Empty,
                    body.Url ?? string.Empty,
                    kinds,
                    actor.Value),
                cancellationToken);
            return Results.Created(
                $"/engagements/{engagement}/webhook-subscriptions/{subscription.Id}",
                WebhookSubscriptionResponse.Of(subscription));
        }
        catch (WebhookSubscriptionRejectedException ex)
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
        WebhookService service,
        CancellationToken cancellationToken)
    {
        if (user.TryGetOperatorId() is null)
            return Results.Unauthorized();
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var subscriptions = await service.ListAsync(engagement, cancellationToken);
        return Results.Ok(new WebhookSubscriptionListResponse(
            subscriptions.Select(WebhookSubscriptionResponse.Of).ToArray()));
    }

    private static async Task<IResult> GetAsync(
        string engagementId,
        string subscriptionId,
        ClaimsPrincipal user,
        WebhookService service,
        CancellationToken cancellationToken)
    {
        if (user.TryGetOperatorId() is null)
            return Results.Unauthorized();
        if (!TryResolve(engagementId, subscriptionId, out var engagement, out var subscription, out var error))
            return error!;

        var stored = await service.FindAsync(engagement, subscription, cancellationToken);
        return stored is null
            ? Results.NotFound(new Problem("The webhook subscription does not exist in this engagement."))
            : Results.Ok(WebhookSubscriptionResponse.Of(stored));
    }

    private static async Task<IResult> EnableAsync(
        string engagementId,
        string subscriptionId,
        ClaimsPrincipal user,
        WebhookService service,
        CancellationToken cancellationToken)
        => await TransitionAsync(engagementId, subscriptionId, user, service, enable: true, cancellationToken);

    private static async Task<IResult> DisableAsync(
        string engagementId,
        string subscriptionId,
        ClaimsPrincipal user,
        WebhookService service,
        CancellationToken cancellationToken)
        => await TransitionAsync(engagementId, subscriptionId, user, service, enable: false, cancellationToken);

    private static async Task<IResult> TransitionAsync(
        string engagementId,
        string subscriptionId,
        ClaimsPrincipal user,
        WebhookService service,
        bool enable,
        CancellationToken cancellationToken)
    {
        var actor = user.TryGetOperatorId();
        if (actor is null)
            return Results.Unauthorized();
        if (!TryResolve(engagementId, subscriptionId, out var engagement, out var subscription, out var error))
            return error!;

        try
        {
            var stored = enable
                ? await service.EnableAsync(engagement, subscription, actor.Value, cancellationToken)
                : await service.DisableAsync(engagement, subscription, actor.Value, cancellationToken);
            return Results.Ok(WebhookSubscriptionResponse.Of(stored));
        }
        catch (InvalidOperationException)
        {
            // The subscription left between the resolve and the transition;
            // the next read says 404 and that is the honest answer.
            return Results.NotFound(new Problem("The webhook subscription does not exist in this engagement."));
        }
    }

    private static async Task<IResult> TestAsync(
        string engagementId,
        string subscriptionId,
        ClaimsPrincipal user,
        WebhookService service,
        CancellationToken cancellationToken)
    {
        var actor = user.TryGetOperatorId();
        if (actor is null)
            return Results.Unauthorized();
        if (!TryResolve(engagementId, subscriptionId, out var engagement, out var subscription, out var error))
            return error!;

        try
        {
            // 200 either way: the outcome is the answer, not an error --
            // a failed test is a successful verification of a dead channel.
            var result = await service.TestDeliverAsync(engagement, subscription, actor.Value, cancellationToken);
            return Results.Ok(new WebhookTestResponse(result.Delivered, result.Outcome));
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound(new Problem("The webhook subscription does not exist in this engagement."));
        }
    }

    private static async Task<IResult> DeleteAsync(
        string engagementId,
        string subscriptionId,
        ClaimsPrincipal user,
        WebhookService service,
        CancellationToken cancellationToken)
    {
        var actor = user.TryGetOperatorId();
        if (actor is null)
            return Results.Unauthorized();
        if (!TryResolve(engagementId, subscriptionId, out var engagement, out var subscription, out var error))
            return error!;

        var deleted = await service.DeleteAsync(engagement, subscription, actor.Value, cancellationToken);
        return deleted
            ? Results.NoContent()
            : Results.NotFound(new Problem("The webhook subscription does not exist in this engagement."));
    }

    private static bool TryResolveKinds(
        string[]? kinds,
        out IReadOnlyList<LiveEventKind> resolved,
        out string error)
    {
        if (kinds is not { Length: > 0 })
        {
            resolved = Array.Empty<LiveEventKind>();
            error = "A webhook subscription names at least one live event kind (eventKinds).";
            return false;
        }

        var parsed = new List<LiveEventKind>(kinds.Length);
        foreach (var named in kinds)
        {
            if (string.IsNullOrWhiteSpace(named)
                || !Enum.TryParse<LiveEventKind>(named, ignoreCase: true, out var kind))
            {
                resolved = Array.Empty<LiveEventKind>();
                error = $"'{named}' is not a live event kind.";
                return false;
            }
            parsed.Add(kind);
        }

        resolved = parsed;
        error = string.Empty;
        return true;
    }

    private static bool TryResolve(
        string engagementId,
        string subscriptionId,
        out EngagementId engagement,
        out WebhookSubscriptionId subscription,
        out IResult? error)
    {
        if (!EngagementId.TryParse(engagementId, out engagement)
            || !WebhookSubscriptionId.TryParse(subscriptionId, out subscription))
        {
            subscription = default;
            error = Results.BadRequest(new Problem("Identifier is not a valid id."));
            return false;
        }

        error = null;
        return true;
    }


    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record CreateWebhookSubscriptionRequest(string? Name, string? Url, string[]? EventKinds);

    public sealed record WebhookSubscriptionResponse(
        string Id,
        string EngagementId,
        string Name,
        string Url,
        IReadOnlyList<string> EventKinds,
        bool Enabled,
        int DeliveryCount,
        DateTimeOffset? LastDeliveredAt,
        int ConsecutiveFailures,
        string? LastFailureReason,
        DateTimeOffset CreatedAt,
        string CreatedBy,
        DateTimeOffset? DisabledAt)
    {
        public static WebhookSubscriptionResponse Of(WebhookSubscription subscription) => new(
            subscription.Id.ToString(),
            subscription.EngagementId.ToString(),
            subscription.Name,
            subscription.Url,
            subscription.EventKinds.Select(k => k.ToString()).ToArray(),
            subscription.Enabled,
            subscription.DeliveryCount,
            subscription.LastDeliveredAt,
            subscription.ConsecutiveFailures,
            subscription.LastFailureReason,
            subscription.CreatedAt,
            subscription.CreatedBy.ToString(),
            subscription.DisabledAt);
    }

    public sealed record WebhookSubscriptionListResponse(IReadOnlyList<WebhookSubscriptionResponse> Subscriptions);

    public sealed record WebhookTestResponse(bool Delivered, string Outcome);
}
