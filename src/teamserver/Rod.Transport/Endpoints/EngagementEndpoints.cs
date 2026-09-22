using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Deployment;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing engagement endpoints: create an engagement and mint a
/// deploy token for it, and list engagements (, the
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
        group.MapGet("/{engagementId}", GetEngagementAsync).WithName(nameof(GetEngagementAsync));
        group.MapPost("/", CreateEngagementAsync)
            .WithName(nameof(CreateEngagementAsync));
        group.MapPut("/{engagementId}", EditEngagementAsync)
            .WithName(nameof(EditEngagementAsync));

        group.MapPost("/{engagementId}/deploy-tokens", MintDeployTokenAsync)
            .WithName(nameof(MintDeployTokenAsync));

        group.MapPost("/{engagementId}/deploy-tokens/{tokenId}:revoke", RevokeDeployTokenAsync)
            .WithName(nameof(RevokeDeployTokenAsync));

        group.MapPut("/{engagementId}/roe", ApplyRoeAsync)
            .WithName(nameof(ApplyRoeAsync));

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
                e.Description,
                e.OwnerId.ToString(),
                owner?.Handle ?? string.Empty,
                e.CreatedAt,
                RoeProfileResponse.From(e.Roe),
                e.FrozenAt,
                e.RetiredAt));
        }

        return Results.Ok(body);
    }

    private static async Task<IResult> GetEngagementAsync(
        string engagementId,
        IEngagementRepository engagements,
        IOperatorRepository operators,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var idValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var engagement = await engagements.FindAsync(new EngagementId(idValue), cancellationToken);
        if (engagement is null)
            return Results.NotFound(new Problem($"Engagement {engagementId} does not exist."));

        var owner = await operators.FindAsync(engagement.OwnerId, cancellationToken);
        return Results.Ok(new EngagementResponse(
            engagement.Id.ToString(),
            engagement.Name,
            engagement.Description,
            engagement.OwnerId.ToString(),
            owner?.Handle ?? string.Empty,
            engagement.CreatedAt,
            RoeProfileResponse.From(engagement.Roe),
            engagement.FrozenAt,
            engagement.RetiredAt));
    }

    private static async Task<IResult> CreateEngagementAsync(
        CreateEngagementRequest body,
        ClaimsPrincipal user,
        EngagementService service,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        // The owner is the authenticated operator, resolved off the session
        // principal rather than named in the body (operator auth). The group already requires authorization,
        // so a present-but-missing claim is a defensive 401, not a normal path.
        var ownerId = user.TryGetOperatorId();
        if (ownerId is null)
            return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new Problem("Engagement name is required."));

        var created = await service.CreateEngagementAsync(
            new CreateEngagementCommand(ownerId.Value, body.Name, body.Description),
            cancellationToken);

        var response = new EngagementResponse(
            created.EngagementId.ToString(),
            created.Name,
            created.Description,
            created.OwnerId.ToString(),
            created.OwnerHandle,
            created.CreatedAt,
            RoeProfileResponse.From(RoeProfile.Unrestricted),
            FrozenAt: null,
            RetiredAt: null);

        // The engagement's own creation is the trail's genesis link (architecture.md
        // Sec 11): attributed to the creating owner, carrying the
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

    private static async Task<IResult> EditEngagementAsync(
        string engagementId,
        EditEngagementRequest body,
        ClaimsPrincipal user,
        IEngagementRepository engagements,
        EngagementService service,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var idValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new Problem("Engagement name is required."));

        // Resolve first so an unknown id is a clean 404; after that the only
        // InvalidOperationException left is the aggregate refusing the edit.
        var existing = await engagements.FindAsync(new EngagementId(idValue), cancellationToken);
        if (existing is null)
            return Results.NotFound(new Problem($"Engagement {engagementId} does not exist."));

        EngagementEdited edited;
        try
        {
            edited = await service.EditEngagementAsync(
                new EditEngagementCommand(new EngagementId(idValue), body.Name, body.Description),
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // The aggregate seals a retired engagement's record.
            return Results.Conflict(new Problem(ex.Message));
        }

        // The edit is recorded (architecture.md Sec 11): attributed to the
        // editing operator, the payload naming the new record's shape. The
        // description text itself stays out of the trail -- it is working
        // notes, not a fact about the target.
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: edited.Engagement.Id.Value,
                operatorId: operatorId.Value.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "edit-engagement",
                kind: AuditEventKind.EngagementUpdated,
                payload: $"name={edited.Engagement.Name} description={(edited.Engagement.Description is null ? "cleared" : "set")}",
                output: null,
                outcome: edited.Engagement.Id.ToString(),
                at: clock.GetUtcNow()),
            cancellationToken);

        return Results.Ok(new EngagementResponse(
            edited.Engagement.Id.ToString(),
            edited.Engagement.Name,
            edited.Engagement.Description,
            edited.Engagement.OwnerId.ToString(),
            edited.OwnerHandle,
            edited.Engagement.CreatedAt,
            RoeProfileResponse.From(edited.Engagement.Roe),
            edited.Engagement.FrozenAt,
            edited.Engagement.RetiredAt));
    }

    private static async Task<IResult> MintDeployTokenAsync(
        string engagementId,
        HttpContext context,
        EngagementService service,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var idValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        // The mint scope rides an optional JSON body: a body-less post keeps
        // the single-use, one-hour default; a batch names how many implants the
        // token may enroll and how long the window stays open. The content-type
        // check (not ContentLength) gates the read, so a chunked body binds too.
        MintDeployTokenRequest? request = null;
        if (context.Request.HasJsonContentType())
        {
            try
            {
                request = await context.Request.ReadFromJsonAsync<MintDeployTokenRequest>(cancellationToken);
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest(new Problem("The mint request body is not valid JSON."));
            }
        }
        if (request?.MaxUses is < 1 or > 10_000)
            return Results.BadRequest(new Problem("maxUses must be between 1 and 10000."));
        if (request?.LifetimeSeconds is < 60 or > 2_592_000)
            return Results.BadRequest(new Problem("lifetimeSeconds must be between 60 and 2592000 (30 days)."));
        TimeSpan? lifetime = request?.LifetimeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

        try
        {
            var minted = await service.MintDeployTokenForOwnerAsync(
                new MintDeployTokenCommand(new EngagementId(idValue), request?.MaxUses, lifetime),
                cancellationToken);

            var response = new DeployTokenResponse(
                minted.DeployTokenId.ToString(),
                minted.EngagementId.ToString(),
                minted.Secret,
                minted.IssuedBy.ToString(),
                minted.IssuedAt,
                minted.ExpiresAt,
                minted.MaxUses);

            // A deploy-token mint is recorded (architecture.md Sec 11):
            // attributed to the minting operator, the payload the token's
            // bounded-use/expiry shape, the outcome the new token id. The secret
            // itself is never recorded -- only the fact that a token was minted.
            await audit.AppendAsync(
                AuditEvent.Fact(
                    eventId: Guid.NewGuid(),
                    engagementId: minted.EngagementId.Value,
                    operatorId: minted.IssuedBy.Value,
                    implantId: Guid.Empty,
                    taskId: Guid.Empty,
                    verb: "mint-deploy-token",
                    kind: AuditEventKind.DeployTokenMinted,
                    payload: $"maxUses={minted.MaxUses} expiresAt={minted.ExpiresAt:O}",
                    output: null,
                    outcome: minted.DeployTokenId.ToString(),
                    at: minted.IssuedAt),
            cancellationToken);

            return Results.Ok(response);
        }
        catch (EngagementClosedException ex)
        {
            // The engagement is frozen for close-out or retired: it mints no
            // deployment tokens (architecture.md Sec 2 step 10).
            return Results.Conflict(new Problem(ex.Message));
        }
        catch (InvalidOperationException)
        {
            // Engagement id parsed but unknown.
            return Results.NotFound(new Problem($"Engagement {engagementId} does not exist."));
        }
    }

    private static async Task<IResult> RevokeDeployTokenAsync(
        string engagementId,
        string tokenId,
        ClaimsPrincipal user,
        IDeployTokenService tokens,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var idValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!Guid.TryParse(tokenId, out var tokenValue))
            return Results.BadRequest(new Problem("Deploy token id is not a valid identifier."));

        // The revocation is idempotent and honest about it: revoking an
        // unknown (already revoked, spent, or never minted) id answers 404 so
        // a fat-fingered id does not read as success.
        if (!await tokens.RevokeAsync(new DeployTokenId(tokenValue), cancellationToken))
            return Results.NotFound(new Problem("Deploy token is not held (unknown, spent, or already revoked)."));

        // The revocation is recorded like every engagement fact (architecture.md
        // Sec 11): attributed to the acting operator, the outcome the revoked
        // token id -- the trail lines up with the mint and the build that
        // baked it.
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: idValue,
                operatorId: operatorId.Value.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "revoke-deploy-token",
                kind: AuditEventKind.DeployTokenRevoked,
                payload: "revokedAt=" + clock.GetUtcNow().ToString("O"),
                output: null,
                outcome: tokenValue.ToString(),
                at: clock.GetUtcNow()),
            cancellationToken);

        return Results.Ok(new RevokedDeployTokenResponse(tokenValue.ToString()));
    }

    private static async Task<IResult> ApplyRoeAsync(
        string engagementId,
        ApplyRoeRequest body,
        ClaimsPrincipal user,
        EngagementService service,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // The applying operator is the authenticated operator; the group
        // already requires authorization, so a present-but-missing claim is a
        // defensive 401, not a normal path.
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();
        if (!Guid.TryParse(engagementId, out var idValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        try
        {
            var applied = await service.ApplyRoeAsync(
                new ApplyRoeCommand(
                    new EngagementId(idValue),
                    new RoeProfile(body.PermittedVerbs, body.PermittedImplants)),
                cancellationToken);

            // The scope change is recorded (architecture.md Sec 9, Sec 11):
            // attributed to the applying operator, the payload the profile's
            // shape, the outcome the engagement id. This is the record the
            // trail shows the scope in force at any moment; every refusal the
            // profile causes is a TaskRoeRefused event against it.
            await audit.AppendAsync(
                AuditEvent.Fact(
                    eventId: Guid.NewGuid(),
                    engagementId: applied.EngagementId.Value,
                    operatorId: operatorId.Value.Value,
                    implantId: Guid.Empty,
                    taskId: Guid.Empty,
                    verb: "apply-roe",
                    kind: AuditEventKind.RoeUpdated,
                    payload: Describe(applied.Profile),
                    output: null,
                    outcome: applied.EngagementId.ToString(),
                    at: clock.GetUtcNow()),
                cancellationToken);

            return Results.Ok(new EngagementScopedRoeResponse(
                applied.EngagementId.ToString(),
                RoeProfileResponse.From(applied.Profile)));
        }
        catch (InvalidOperationException)
        {
            // Engagement id parsed but unknown.
            return Results.NotFound(new Problem($"Engagement {engagementId} does not exist."));
        }
    }

    // One line describing the scope in force -- the audit payload for an ROE
    // update and the human-readable form of the profile.
    internal static string Describe(RoeProfile profile)
    {
        var verbs = profile.PermittedVerbs.Count == 0 ? "*" : string.Join(",", profile.PermittedVerbs);
        var targets = profile.PermittedImplants.Count == 0 ? "*" : string.Join(",", profile.PermittedImplants);
        return $"permittedVerbs={verbs} permittedTargets={targets}";
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    // The owner is the authenticated operator; only the engagement name is
    // supplied by the caller.
    public sealed record CreateEngagementRequest(string Name, string? Description = null);

    // The edit replaces the working record whole: name and description. A null
    // description clears it; an empty name is rejected before the service is
    // reached.
    public sealed record EditEngagementRequest(string Name, string? Description = null);

    public sealed record EngagementResponse(
        string EngagementId,
        string Name,
        string? Description,
        string OwnerId,
        string OwnerHandle,
        DateTimeOffset CreatedAt,
        RoeProfileResponse Roe,
        DateTimeOffset? FrozenAt = null,
        DateTimeOffset? RetiredAt = null);

    // The ROE scope request: two allow-lists, each empty (or omitted) meaning
    // unrestricted on that dimension.
    public sealed record ApplyRoeRequest(
        IReadOnlyList<string>? PermittedVerbs,
        IReadOnlyList<string>? PermittedImplants);

    public sealed record RoeProfileResponse(
        IReadOnlyList<string> PermittedVerbs,
        IReadOnlyList<string> PermittedImplants)
    {
        public static RoeProfileResponse From(RoeProfile profile)
            => new(profile.PermittedVerbs, profile.PermittedImplants);
    }

    public sealed record EngagementScopedRoeResponse(
        string EngagementId,
        RoeProfileResponse Roe);

    /// <summary>
    /// The optional mint scope: how many implants the token may enroll (each
    /// spend one use) and how long the mint stays redeemable. Absent values
    /// keep the single-use, one-hour default.
    /// </summary>
    public sealed record MintDeployTokenRequest(int? MaxUses, long? LifetimeSeconds);

    /// <summary>Result of revoking a deploy token: the id that stopped working.</summary>
    public sealed record RevokedDeployTokenResponse(string DeployTokenId);

    public sealed record DeployTokenResponse(
        string DeployTokenId,
        string EngagementId,
        string Secret,
        string IssuedBy,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        int MaxUses);

}
