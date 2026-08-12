using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing engagement endpoints: create an engagement and mint a
/// stager token for it (roadmap M1.1), and list engagements (roadmap M1.5, the
/// operator UI). DTOs live here, in transport, so the core stays serialization-
/// and protocol-free (AGENTS.md Sec 5).
/// </summary>
public static class EngagementEndpoints
{
    public static IEndpointRouteBuilder MapEngagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Operator-facing: every engagement route requires an authenticated
        // operator session (cookie auth wired via AddRodOperatorAuth). The
        // implant-facing enrollment path is mapped separately and stays anonymous.
        var group = endpoints.MapGroup("/engagements").RequireAuthorization();

        group.MapGet("/", ListEngagementsAsync).WithName(nameof(ListEngagementsAsync));
        group.MapPost("/", CreateEngagementAsync)
            .WithName(nameof(CreateEngagementAsync));

        group.MapPost("/{engagementId}/stager-tokens", MintStagerTokenAsync)
            .WithName(nameof(MintStagerTokenAsync));

        return endpoints;
    }

    private static async Task<IResult> ListEngagementsAsync(
        IEngagementRepository engagements,
        IOperatorRepository operators,
        CancellationToken cancellationToken)
    {
        var all = await engagements.ListAsync(cancellationToken);

        // The owner handle lives on the Operator, not the engagement. Resolve it
        // per engagement; an unknown owner (engagement predates the operator) is
        // surfaced as empty rather than failing the whole list.
        var body = new List<EngagementResponse>(all.Count);
        foreach (var e in all)
        {
            var owner = await operators.FindAsync(e.OwnerId, cancellationToken);
            body.Add(new EngagementResponse(
                e.Id.ToString(),
                e.Name,
                e.OwnerId.ToString(),
                owner?.Handle ?? string.Empty,
                e.CreatedAt));
        }

        return Results.Ok(body);
    }

    private static async Task<IResult> CreateEngagementAsync(
        CreateEngagementRequest body,
        ClaimsPrincipal user,
        EngagementService service,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        // The owner is the authenticated operator, resolved off the session
        // principal rather than named in the body (operator auth,
        // production-hardening todo). The group already requires authorization,
        // so a present-but-missing claim is a defensive 401, not a normal path.
        var ownerId = user.TryGetOperatorId();
        if (ownerId is null)
            return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new Problem("Engagement name is required."));

        var created = await service.CreateEngagementAsync(
            new CreateEngagementCommand(ownerId.Value, body.Name),
            cancellationToken);

        var response = new EngagementResponse(
            created.EngagementId.ToString(),
            created.Name,
            created.OwnerId.ToString(),
            created.OwnerHandle,
            created.CreatedAt);

        // The engagement's own creation is the trail's genesis link (architecture.md
        // Sec 11, roadmap M6.1): attributed to the creating owner, carrying the
        // name in its payload and the new engagement id as its outcome. It is the
        // first event in this engagement's chain, so it follows the genesis hash.
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: created.EngagementId.Value,
                operatorId: created.OwnerId.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "create-engagement",
                kind: AuditEventKind.EngagementCreated,
                payload: created.Name,
                output: null,
                outcome: created.EngagementId.ToString(),
                at: created.CreatedAt),
            cancellationToken);

        return Results.Created($"/engagements/{response.EngagementId}", response);
    }

    private static async Task<IResult> MintStagerTokenAsync(
        string engagementId,
        EngagementService service,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var idValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        try
        {
            var minted = await service.MintStagerTokenForOwnerAsync(
                new MintStagerTokenCommand(new EngagementId(idValue)),
                cancellationToken);

            var response = new StagerTokenResponse(
                minted.StagerTokenId.ToString(),
                minted.EngagementId.ToString(),
                minted.Secret,
                minted.IssuedBy.ToString(),
                minted.IssuedAt,
                minted.ExpiresAt,
                minted.MaxUses);

            // A stager-token mint is recorded (architecture.md Sec 11, roadmap
            // M6.1): attributed to the minting operator, the payload the token's
            // bounded-use/expiry shape, the outcome the new token id. The secret
            // itself is never recorded -- only the fact that a token was minted.
            await audit.AppendAsync(
                AuditEvent.Fact(
                    eventId: Guid.NewGuid(),
                    engagementId: minted.EngagementId.Value,
                    operatorId: minted.IssuedBy.Value,
                    implantId: Guid.Empty,
                    taskId: Guid.Empty,
                    verb: "mint-stager-token",
                    kind: AuditEventKind.StagerTokenMinted,
                    payload: $"maxUses={minted.MaxUses} expiresAt={minted.ExpiresAt:O}",
                    output: null,
                    outcome: minted.StagerTokenId.ToString(),
                    at: minted.IssuedAt),
            cancellationToken);

            return Results.Ok(response);
        }
        catch (InvalidOperationException)
        {
            // Engagement id parsed but unknown.
            return Results.NotFound(new Problem($"Engagement {engagementId} does not exist."));
        }
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    // The owner is the authenticated operator; only the engagement name is
    // supplied by the caller.
    public sealed record CreateEngagementRequest(string Name);

    public sealed record EngagementResponse(
        string EngagementId,
        string Name,
        string OwnerId,
        string OwnerHandle,
        DateTimeOffset CreatedAt);

    public sealed record StagerTokenResponse(
        string StagerTokenId,
        string EngagementId,
        string Secret,
        string IssuedBy,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        int MaxUses);

    public sealed record Problem(string Error);
}
