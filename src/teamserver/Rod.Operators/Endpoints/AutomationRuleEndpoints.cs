using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Automation;
using Rod.CoreState.Engagements;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.Operators.Automation;
using Task = System.Threading.Tasks.Task;

namespace Rod.Operators.Endpoints;

/// <summary>
/// The automation-rule surface (architecture.md Sec 10.4), engagement-scoped
/// under <c>/engagements/{engagementId}/automation-rules</c>. The routes live
/// in the operator layer with the SSE stream -- transport may not reference
/// the engine's layer, and rules are the engine's domain -- and every
/// mutation lands in the engagement trail through
/// <see cref="AutomationService"/>.
/// </summary>
public static class AutomationRuleEndpoints
{
    public static IEndpointRouteBuilder MapAutomationRuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/engagements/{engagementId}/automation-rules")
            .RequireAuthorization().AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Read));

        group.MapPost(string.Empty, CreateAsync).AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("CreateAutomationRule");
        group.MapGet(string.Empty, ListAsync).WithName("ListAutomationRules");
        group.MapGet("/{ruleId}", GetAsync).WithName("GetAutomationRule");
        group.MapPost("/{ruleId}:enable", EnableAsync).AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("EnableAutomationRule");
        group.MapPost("/{ruleId}:disable", DisableAsync).AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("DisableAutomationRule");
        group.MapDelete("/{ruleId}", DeleteAsync).AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("DeleteAutomationRule");
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        string engagementId,
        CreateAutomationRuleRequest body,
        ClaimsPrincipal user,
        AutomationService service,
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
        if (body is null || body.Trigger is null)
            return Results.BadRequest(new Problem("A rule needs a trigger."));

        if (!TryResolveTrigger(body.Trigger, out var trigger, out var triggerError))
            return Results.BadRequest(new Problem(triggerError));
        ImplantId? onlyImplant = null;
        if (body.OnlyImplantId is { Length: > 0 } filter)
        {
            if (!ImplantId.TryParse(filter, out var parsedFilter))
                return Results.BadRequest(new Problem("The event-filter implant id is not a valid identifier."));
            onlyImplant = parsedFilter;
        }
        if (!ImplantId.TryParse(body.TargetImplantId, out var target))
            return Results.BadRequest(new Problem("Target implant id is not a valid identifier."));

        try
        {
            var rule = await service.CreateAsync(
                new AutomationService.CreateRuleCommand(
                    engagement,
                    body.Name ?? string.Empty,
                    trigger,
                    onlyImplant,
                    body.CompletedVerb,
                    target,
                    body.Verb ?? string.Empty,
                    body.Arguments ?? string.Empty,
                    body.CooldownSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                    body.MaxFirings,
                    actor.Value),
                cancellationToken);
            return Results.Created(
                $"/engagements/{engagement}/automation-rules/{rule.Id}",
                AutomationRuleResponse.Of(rule));
        }
        catch (AutomationRuleRejectedException ex)
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
        AutomationService service,
        CancellationToken cancellationToken)
    {
        if (user.TryGetOperatorId() is null)
            return Results.Unauthorized();
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var rules = await service.ListAsync(engagement, cancellationToken);
        return Results.Ok(new AutomationRuleListResponse(rules.Select(AutomationRuleResponse.Of).ToArray()));
    }

    private static async Task<IResult> GetAsync(
        string engagementId,
        string ruleId,
        ClaimsPrincipal user,
        AutomationService service,
        CancellationToken cancellationToken)
    {
        if (user.TryGetOperatorId() is null)
            return Results.Unauthorized();
        if (!TryResolve(engagementId, ruleId, out var engagement, out var rule, out var error))
            return error!;

        var stored = await service.FindAsync(engagement, rule, cancellationToken);
        return stored is null
            ? Results.NotFound(new Problem("The automation rule does not exist in this engagement."))
            : Results.Ok(AutomationRuleResponse.Of(stored));
    }

    private static async Task<IResult> EnableAsync(
        string engagementId,
        string ruleId,
        ClaimsPrincipal user,
        AutomationService service,
        CancellationToken cancellationToken)
        => await TransitionAsync(engagementId, ruleId, user, service, enable: true, cancellationToken);

    private static async Task<IResult> DisableAsync(
        string engagementId,
        string ruleId,
        ClaimsPrincipal user,
        AutomationService service,
        CancellationToken cancellationToken)
        => await TransitionAsync(engagementId, ruleId, user, service, enable: false, cancellationToken);

    private static async Task<IResult> TransitionAsync(
        string engagementId,
        string ruleId,
        ClaimsPrincipal user,
        AutomationService service,
        bool enable,
        CancellationToken cancellationToken)
    {
        var actor = user.TryGetOperatorId();
        if (actor is null)
            return Results.Unauthorized();
        if (!TryResolve(engagementId, ruleId, out var engagement, out var rule, out var error))
            return error!;

        try
        {
            var stored = enable
                ? await service.EnableAsync(engagement, rule, actor.Value, cancellationToken)
                : await service.DisableAsync(engagement, rule, actor.Value, cancellationToken);
            return Results.Ok(AutomationRuleResponse.Of(stored));
        }
        catch (InvalidOperationException)
        {
            // The rule left between the resolve and the transition; the next
            // read says 404 and that is the honest answer.
            return Results.NotFound(new Problem("The automation rule does not exist in this engagement."));
        }
    }

    private static async Task<IResult> DeleteAsync(
        string engagementId,
        string ruleId,
        ClaimsPrincipal user,
        AutomationService service,
        CancellationToken cancellationToken)
    {
        var actor = user.TryGetOperatorId();
        if (actor is null)
            return Results.Unauthorized();
        if (!TryResolve(engagementId, ruleId, out var engagement, out var rule, out var error))
            return error!;

        var deleted = await service.DeleteAsync(engagement, rule, actor.Value, cancellationToken);
        return deleted
            ? Results.NoContent()
            : Results.NotFound(new Problem("The automation rule does not exist in this engagement."));
    }

    private static bool TryResolveTrigger(
        AutomationTriggerRequest trigger,
        out AutomationTrigger resolved,
        out string error)
    {
        switch ((trigger.Kind ?? string.Empty).ToLowerInvariant())
        {
            case "interval":
                if (trigger.IntervalSeconds is not > 0)
                {
                    resolved = null!;
                    error = "An interval trigger needs intervalSeconds greater than zero.";
                    return false;
                }
                resolved = new AutomationTrigger.Interval(TimeSpan.FromSeconds(trigger.IntervalSeconds.Value));
                error = string.Empty;
                return true;
            case "event":
                if (string.IsNullOrWhiteSpace(trigger.EventKind)
                    || !Enum.TryParse<LiveEventKind>(trigger.EventKind, ignoreCase: true, out var kind))
                {
                    resolved = null!;
                    error = "An event trigger names a live event kind (eventKind).";
                    return false;
                }
                resolved = new AutomationTrigger.Event(kind);
                error = string.Empty;
                return true;
            default:
                resolved = null!;
                error = "Trigger kind must be 'interval' or 'event'.";
                return false;
        }
    }

    private static bool TryResolve(
        string engagementId,
        string ruleId,
        out EngagementId engagement,
        out AutomationRuleId rule,
        out IResult? error)
    {
        if (!EngagementId.TryParse(engagementId, out engagement)
            || !AutomationRuleId.TryParse(ruleId, out rule))
        {
            rule = default;
            error = Results.BadRequest(new Problem("Identifier is not a valid id."));
            return false;
        }

        error = null;
        return true;
    }


    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record AutomationTriggerRequest(string? Kind, int? IntervalSeconds, string? EventKind);

    public sealed record CreateAutomationRuleRequest(
        string? Name,
        AutomationTriggerRequest? Trigger,
        string? OnlyImplantId,
        string? CompletedVerb,
        string? TargetImplantId,
        string? Verb,
        string? Arguments,
        int? CooldownSeconds,
        int? MaxFirings);

    public sealed record AutomationRuleResponse(
        string Id,
        string EngagementId,
        string Name,
        string TriggerKind,
        long? IntervalSeconds,
        string? EventKind,
        string? OnlyImplantId,
        string? CompletedVerb,
        string TargetImplantId,
        string Verb,
        string Arguments,
        int CooldownSeconds,
        int MaxFirings,
        bool Enabled,
        int FireCount,
        DateTimeOffset? LastFiredAt,
        DateTimeOffset? NextFireAt,
        int ConsecutiveRefusals,
        DateTimeOffset CreatedAt,
        string CreatedBy,
        DateTimeOffset? DisabledAt)
    {
        public static AutomationRuleResponse Of(AutomationRule rule) => new(
            rule.Id.ToString(),
            rule.EngagementId.ToString(),
            rule.Name,
            rule.Trigger is AutomationTrigger.Interval ? "interval" : "event",
            rule.Trigger is AutomationTrigger.Interval interval ? (long)interval.Every.TotalSeconds : null,
            rule.Trigger is AutomationTrigger.Event(var kind) ? kind.ToString() : null,
            rule.OnlyImplant?.ToString(),
            rule.CompletedVerb,
            rule.TargetImplant.ToString(),
            rule.Verb,
            rule.Arguments,
            (int)rule.Cooldown.TotalSeconds,
            rule.MaxFirings,
            rule.Enabled,
            rule.FireCount,
            rule.LastFiredAt,
            rule.NextFireAt,
            rule.ConsecutiveRefusals,
            rule.CreatedAt,
            rule.CreatedBy.ToString(),
            rule.DisabledAt);
    }

    public sealed record AutomationRuleListResponse(IReadOnlyList<AutomationRuleResponse> Rules);
}
