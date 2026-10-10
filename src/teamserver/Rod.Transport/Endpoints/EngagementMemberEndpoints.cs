using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The engagement's member roster (architecture.md Sec 3, the membership
/// model): the owner grants, retiers, and removes the operators who may see
/// and work the engagement. Reads are a member's own surface (the roster
/// names the crew you work with); every mutation is the owner's alone. A
/// stranger resolves to the same 404 a missing engagement renders -- the
/// engagement's existence is itself information. Each change lands in the
/// engagement's audit trail (membership is engagement state, not account
/// state) and beats one live event so connected consoles refresh on the
/// spot.
/// </summary>
public static class EngagementMemberEndpoints
{
    public static IEndpointRouteBuilder MapEngagementMemberEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/engagements")
            .RequireAuthorization();

        group.MapGet("/{engagementId}/members", ListMembersAsync).WithName(nameof(ListMembersAsync));
        group.MapPost("/{engagementId}/members", AddMemberAsync).WithName(nameof(AddMemberAsync));
        group.MapPut("/{engagementId}/members/{operatorId}", SetMemberRoleAsync).WithName(nameof(SetMemberRoleAsync));
        group.MapDelete("/{engagementId}/members/{operatorId}", RemoveMemberAsync).WithName(nameof(RemoveMemberAsync));

        return endpoints;
    }

    private static async Task<IResult> ListMembersAsync(
        string engagementId,
        ClaimsPrincipal user,
        IEngagementRepository engagements,
        IEngagementMembershipStore memberships,
        EngagementAccessResolver access,
        IOperatorRepository operators,
        CancellationToken cancellationToken)
    {
        var caller = user.TryGetOperatorId();
        if (caller is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var idValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var engagementIdValue = new EngagementId(idValue);
        var standing = await access.ResolveAsync(engagementIdValue, caller.Value, cancellationToken);
        if (!standing.EngagementExists || standing.Level == EngagementAccessLevel.None)
            return Results.NotFound(new Problem($"Engagement {engagementId} does not exist."));

        // The roster renders the owner first -- ownership is not a membership
        // row, but the crew view that includes the owner is the honest one.
        var engagement = await engagements.FindAsync(engagementIdValue, cancellationToken);
        var rows = new List<EngagementMemberResponse>
        {
            await ToRowAsync(engagement!.OwnerId, "owner", engagement.CreatedAt, operators, cancellationToken),
        };
        foreach (var membership in await memberships.ListAsync(engagementIdValue, cancellationToken))
        {
            rows.Add(await ToRowAsync(
                membership.OperatorId,
                EngagementRoles.ToName(membership.Role),
                membership.AddedAt,
                operators,
                cancellationToken));
        }

        return Results.Ok(rows);
    }

    private static async Task<IResult> AddMemberAsync(
        string engagementId,
        AddMemberRequest? body,
        ClaimsPrincipal user,
        EngagementAccessResolver access,
        IEngagementMembershipStore memberships,
        IOperatorRepository operators,
        IAuditStore audit,
        ILiveEventBus bus,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = user.TryGetOperatorId();
        if (caller is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var idValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!EngagementRoles.TryFromName(body?.Role, out var role))
            return Results.BadRequest(new Problem("Role must be 'reader' or 'writer'."));
        if (string.IsNullOrWhiteSpace(body?.Handle))
            return Results.BadRequest(new Problem("Handle is required."));

        // The gate ladder: a stranger sees the 404 (concealment), a member
        // without ownership is refused (403), and only the owner grants.
        var engagementIdValue = new EngagementId(idValue);
        var standing = await access.ResolveAsync(engagementIdValue, caller.Value, cancellationToken);
        if (!standing.EngagementExists || standing.Level == EngagementAccessLevel.None)
            return Results.NotFound(new Problem($"Engagement {engagementId} does not exist."));
        if (standing.Level != EngagementAccessLevel.Owner)
            return Results.Forbid();

        // The invite names a handle, not an id -- the operator-facing shape;
        // the roster is small and handles are the shared vocabulary.
        var target = await operators.FindByHandleAsync(body.Handle.Trim(), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator '{body.Handle.Trim()}' does not exist."));
        if (target.Id == standing.OwnerId)
            return Results.Conflict(new Problem(
                "The engagement owner holds access by creation; ownership is not a membership to grant."));

        var existing = await memberships.FindAsync(engagementIdValue, target.Id, cancellationToken);
        if (existing is not null)
            return Results.Conflict(new Problem(
                $"Operator '{target.Handle}' is already a member (role '{EngagementRoles.ToName(existing.Role)}')."));

        var membership = new EngagementMembership(engagementIdValue, target.Id, role, clock.GetUtcNow());
        await memberships.SaveAsync(membership, cancellationToken);

        await RecordAsync(
            audit,
            bus,
            clock,
            engagementIdValue,
            caller.Value,
            AuditEventKind.EngagementMemberAdded,
            $"handle={target.Handle} role={EngagementRoles.ToName(role)}",
            target.Id,
            $"action=added handle={target.Handle} role={EngagementRoles.ToName(role)}",
            cancellationToken);

        return Results.Created(
            $"/engagements/{engagementId}/members/{target.Id}",
            await ToRowAsync(target.Id, EngagementRoles.ToName(role), membership.AddedAt, operators, cancellationToken));
    }

    private static async Task<IResult> SetMemberRoleAsync(
        string engagementId,
        string operatorId,
        SetMemberRoleRequest? body,
        ClaimsPrincipal user,
        EngagementAccessResolver access,
        IEngagementMembershipStore memberships,
        IOperatorRepository operators,
        IAuditStore audit,
        ILiveEventBus bus,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = user.TryGetOperatorId();
        if (caller is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var idValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));
        if (!EngagementRoles.TryFromName(body?.Role, out var role))
            return Results.BadRequest(new Problem("Role must be 'reader' or 'writer'."));

        var engagementIdValue = new EngagementId(idValue);
        var standing = await access.ResolveAsync(engagementIdValue, caller.Value, cancellationToken);
        if (!standing.EngagementExists || standing.Level == EngagementAccessLevel.None)
            return Results.NotFound(new Problem($"Engagement {engagementId} does not exist."));
        if (standing.Level != EngagementAccessLevel.Owner)
            return Results.Forbid();

        var targetId = new OperatorId(operatorValue);
        if (targetId == standing.OwnerId)
            return Results.Conflict(new Problem(
                "The engagement owner holds access by creation; there is no role to change."));

        var existing = await memberships.FindAsync(engagementIdValue, targetId, cancellationToken);
        if (existing is null)
            return Results.NotFound(new Problem($"Operator {operatorId} is not a member of this engagement."));

        // An unchanged assignment is a quiet no-op: the state already says it.
        if (existing.Role != role)
        {
            await memberships.SaveAsync(existing with { Role = role }, cancellationToken);
            var target = await operators.FindAsync(targetId, cancellationToken);
            await RecordAsync(
                audit,
                bus,
                clock,
                engagementIdValue,
                caller.Value,
                AuditEventKind.EngagementMemberRoleChanged,
                $"handle={target?.Handle ?? targetId.ToString()} role={EngagementRoles.ToName(existing.Role)}->{EngagementRoles.ToName(role)}",
                targetId,
                $"action=role handle={target?.Handle ?? targetId.ToString()} role={EngagementRoles.ToName(role)}",
                cancellationToken);
        }

        return Results.Ok(await ToRowAsync(
            targetId,
            EngagementRoles.ToName(role),
            existing.AddedAt,
            operators,
            cancellationToken));
    }

    private static async Task<IResult> RemoveMemberAsync(
        string engagementId,
        string operatorId,
        ClaimsPrincipal user,
        EngagementAccessResolver access,
        IEngagementMembershipStore memberships,
        IOperatorRepository operators,
        IAuditStore audit,
        ILiveEventBus bus,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = user.TryGetOperatorId();
        if (caller is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var idValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));

        var engagementIdValue = new EngagementId(idValue);
        var standing = await access.ResolveAsync(engagementIdValue, caller.Value, cancellationToken);
        if (!standing.EngagementExists || standing.Level == EngagementAccessLevel.None)
            return Results.NotFound(new Problem($"Engagement {engagementId} does not exist."));
        if (standing.Level != EngagementAccessLevel.Owner)
            return Results.Forbid();

        var targetId = new OperatorId(operatorValue);
        if (targetId == standing.OwnerId)
            return Results.Conflict(new Problem(
                "An engagement always keeps exactly one owner; ownership cannot be removed, only retired with the engagement."));

        // Removing a non-member is a 404, not a quiet 200: a fat-fingered id
        // must not read as success (the revoke-token route's own posture).
        if (!await memberships.RemoveAsync(engagementIdValue, targetId, cancellationToken))
            return Results.NotFound(new Problem($"Operator {operatorId} is not a member of this engagement."));

        var target = await operators.FindAsync(targetId, cancellationToken);
        await RecordAsync(
            audit,
            bus,
            clock,
            engagementIdValue,
            caller.Value,
            AuditEventKind.EngagementMemberRemoved,
            $"handle={target?.Handle ?? targetId.ToString()}",
            targetId,
            $"action=removed handle={target?.Handle ?? targetId.ToString()}",
            cancellationToken);

        return Results.Ok(new { operatorId = targetId.ToString() });
    }

    // One membership change = one audit fact + one live beat, attributed to
    // the acting owner. The payload names the handle and role so both the
    // trail and the connected consoles render it without a lookup.
    private static async Task RecordAsync(
        IAuditStore audit,
        ILiveEventBus bus,
        TimeProvider clock,
        EngagementId engagementId,
        OperatorId actingOwner,
        AuditEventKind kind,
        string auditPayload,
        OperatorId subject,
        string livePayload,
        CancellationToken cancellationToken)
    {
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagementId.Value,
                operatorId: actingOwner.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "manage-members",
                kind: kind,
                payload: auditPayload,
                output: null,
                outcome: subject.ToString(),
                at: clock.GetUtcNow()),
            cancellationToken);
        await bus.PublishAsync(
            LiveEvent.Membership(engagementId, actingOwner, livePayload, clock.GetUtcNow()),
            cancellationToken);
    }

    private static async Task<EngagementMemberResponse> ToRowAsync(
        OperatorId operatorId,
        string role,
        DateTimeOffset addedAt,
        IOperatorRepository operators,
        CancellationToken cancellationToken)
    {
        var op = await operators.FindAsync(operatorId, cancellationToken);
        return new EngagementMemberResponse(
            operatorId.ToString(),
            op?.Handle ?? string.Empty,
            op?.DisplayName ?? string.Empty,
            role,
            addedAt);
    }
}

/// <summary>The invite: the operator's handle and the role being granted.</summary>
public sealed record AddMemberRequest(string? Handle, string? Role);

/// <summary>The re-tier: the member's complete new role.</summary>
public sealed record SetMemberRoleRequest(string? Role);

/// <summary>
/// One roster row: the operator's identity, the role they hold in this
/// engagement ("owner" included -- ownership renders in the crew view), and
/// when the grant was made.
/// </summary>
public sealed record EngagementMemberResponse(
    string OperatorId,
    string Handle,
    string DisplayName,
    string Role,
    DateTimeOffset AddedAt);
