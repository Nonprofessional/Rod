using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
        var group = endpoints.MapGroup("/engagements");

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
        EngagementService service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new Problem("Engagement name is required."));
        if (string.IsNullOrWhiteSpace(body.OwnerHandle))
            return Results.BadRequest(new Problem("Owner handle is required."));
        if (string.IsNullOrWhiteSpace(body.OwnerDisplayName))
            return Results.BadRequest(new Problem("Owner display name is required."));
        if (body.OwnerId is null)
            return Results.BadRequest(new Problem("Owner id is required."));

        var created = await service.CreateEngagementAsync(
            new CreateEngagementCommand(
                new OperatorId(body.OwnerId.Value),
                body.OwnerHandle,
                body.OwnerDisplayName,
                body.Name),
            cancellationToken);

        var response = new EngagementResponse(
            created.EngagementId.ToString(),
            created.Name,
            created.OwnerId.ToString(),
            created.OwnerHandle,
            created.CreatedAt);

        return Results.Created($"/engagements/{response.EngagementId}", response);
    }

    private static async Task<IResult> MintStagerTokenAsync(
        string engagementId,
        EngagementService service,
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

            return Results.Ok(response);
        }
        catch (InvalidOperationException)
        {
            // Engagement id parsed but unknown.
            return Results.NotFound(new Problem($"Engagement {engagementId} does not exist."));
        }
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record CreateEngagementRequest(
        Guid? OwnerId,
        string OwnerHandle,
        string OwnerDisplayName,
        string Name);

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
