using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Operators;
using Rod.CoreState.Operators.Interaction;
using Rod.CoreState.ShellSessions;
using Rod.CoreState.Tasks;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The engagement's interaction claims (architecture.md Sec 4.5): the
/// operator surface of exclusive ownership on the typing halves -- a live
/// channel task's input and relay binds, a caught shell's input. Acquire is
/// explicit here and implicit in the input routes (the first post takes an
/// unclaimed surface); the listing is the visibility half, seeding the hello
/// frame a console reads and reconciling it afterwards.
/// </summary>
public static class ClaimEndpoints
{
    public static IEndpointRouteBuilder MapClaimEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Reads need the viewing scope; acquiring and releasing are acting.
        var group = endpoints
            .MapGroup("/engagements/{engagementId}/claims")
            .RequireAuthorization().AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Read));

        group.MapGet("/", ListAsync).WithName("ListInteractionClaims");
        group.MapPost("/", AcquireAsync)
            .AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("AcquireInteractionClaim");
        group.MapDelete("/{kind}/{surfaceId}", ReleaseAsync)
            .AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("ReleaseInteractionClaim");

        return endpoints;
    }

    // The listing, with its lazy cleanup: a claim on a surface that ended is
    // inert (the surface's own state refuses input before the claim is
    // consulted) and would only linger as roster noise, so the read drops
    // such claims when it sees them -- the claims' one reaper beside the
    // disconnect release.
    private static async Task<IResult> ListAsync(
        string engagementId,
        OperatorInteractionService interaction,
        ITaskRepository tasks,
        IShellSessionRegistry shells,
        CancellationToken cancellationToken)
    {
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var live = new List<InteractionClaim>();
        foreach (var claim in await interaction.ListAsync(engagement, cancellationToken))
        {
            if (await SurfaceEndedAsync(claim, tasks, shells, cancellationToken))
                await interaction.DropAsync(engagement, claim.Surface, claim.SurfaceId, cancellationToken);
            else
                live.Add(claim);
        }

        return Results.Ok(live.Select(ToResponse).ToArray());
    }

    // A channel is claimable from issue (Queued) through dispatch; every
    // terminal state ended it. A caught shell is claimable exactly while it
    // is Live. A surface whose row has vanished entirely is ended too.
    // (TaskStatus is fully qualified: the BCL's same-named status enum is one
    // implicit using away.)
    private static async Task<bool> SurfaceEndedAsync(
        InteractionClaim claim,
        ITaskRepository tasks,
        IShellSessionRegistry shells,
        CancellationToken cancellationToken)
        => claim.Surface switch
        {
            InteractionSurface.ChannelTask => await tasks.FindAsync(new TaskId(claim.SurfaceId), cancellationToken)
                is not { Status: Rod.CoreState.Tasks.TaskStatus.Queued or Rod.CoreState.Tasks.TaskStatus.Dispatched },
            InteractionSurface.ShellSession => await shells.FindAsync(new ShellSessionId(claim.SurfaceId), cancellationToken)
                is not { Status: ShellSessionStatus.Live },
            _ => true,
        };

    // The explicit acquire: a console takes a surface before typing to signal
    // intent (the input routes' implicit acquire covers the unclaimed case
    // either way). Refused with the holder named when another operator holds
    // the surface; idempotent for the holder.
    private static async Task<IResult> AcquireAsync(
        string engagementId,
        ClaimRequest? body,
        ClaimsPrincipal user,
        OperatorInteractionService interaction,
        IOperatorRepository operators,
        CancellationToken cancellationToken)
    {
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var surfaceId = body?.SurfaceId();
        if (!InteractionSurfaceExtensions.TryParseKind(body?.Kind, out var surface) || surfaceId is null)
            return Results.BadRequest(
                new Problem("A claim names one surface: kind must be 'channel' or 'shell' with its task or session id."));

        var result = await interaction.TryAcquireAsync(
            engagement, surface, surfaceId.Value, operatorId.Value, cancellationToken);
        if (!result.Acquired)
        {
            var holder = result.HeldBy!;
            var handle = await ResolveHandleAsync(operators, holder.OperatorId, cancellationToken);
            return Results.Conflict(new Problem(
                $"Held by {handle} since {holder.AcquiredAt:O}."));
        }

        return Results.Ok(ToResponse(result.Claim!));
    }

    // The explicit release: holder-only (a peer cannot drop a claim they do
    // not hold; ending the surface itself is the safety valve that never
    // needed the claim). A no-op release of an unheld surface is a 404 -- the
    // claim the caller named does not exist.
    private static async Task<IResult> ReleaseAsync(
        string engagementId,
        string kind,
        string surfaceId,
        ClaimsPrincipal user,
        OperatorInteractionService interaction,
        CancellationToken cancellationToken)
    {
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!InteractionSurfaceExtensions.TryParseKind(kind, out var surface))
            return Results.BadRequest(new Problem("Claim kind must be 'channel' or 'shell'."));
        if (!Guid.TryParse(surfaceId, out var surfaceValue))
            return Results.BadRequest(new Problem("Surface id is not a valid identifier."));

        var released = await interaction.ReleaseAsync(
            engagement, surface, surfaceValue, operatorId.Value, cancellationToken);
        return released ? Results.NoContent() : Results.NotFound(new Problem("No claim is held on that surface."));
    }

    private static async Task<string> ResolveHandleAsync(
        IOperatorRepository operators,
        OperatorId operatorId,
        CancellationToken cancellationToken)
        => (await operators.FindAsync(operatorId, cancellationToken))?.Handle
           ?? operatorId.ToString();

    private static ClaimResponse ToResponse(InteractionClaim claim)
        => new(
            claim.Surface.RouteKind(),
            claim.SurfaceWireId(),
            claim.OperatorId.ToString(),
            claim.AcquiredAt);

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    /// <summary>
    /// The acquire request: the surface kind (<c>channel</c> or <c>shell</c>)
    /// and the task or shell-session id it names.
    /// </summary>
    public sealed record ClaimRequest(string? Kind, string? TaskId = null, string? ShellId = null)
    {
        /// <summary>The surface id the request names, whichever field carried it.</summary>
        public Guid? SurfaceId()
        {
            if (Guid.TryParse(TaskId, out var task))
                return task;
            if (Guid.TryParse(ShellId, out var shell))
                return shell;
            return null;
        }
    }

    /// <summary>
    /// One held claim: the surface (kind and id), the holding operator, and
    /// since when. The holder's handle resolves in the console from the
    /// roster, so the wire carries the id only.
    /// </summary>
    public sealed record ClaimResponse(string Kind, string SurfaceId, string OperatorId, DateTimeOffset AcquiredAt);
}
