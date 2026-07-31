using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Application;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The first operator-facing HTTP endpoints (roadmap M1.1): create an
/// engagement and mint a stager token for it. DTOs live here, in transport, so
/// the core stays serialization- and protocol-free (AGENTS.md Sec 5).
/// </summary>
public static class EngagementEndpoints
{
    public static IEndpointRouteBuilder MapEngagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/engagements");

        group.MapPost("/", CreateEngagementAsync)
            .WithName(nameof(CreateEngagementAsync));

        group.MapPost("/{engagementId}/stager-tokens", MintStagerTokenAsync)
            .WithName(nameof(MintStagerTokenAsync));

        return endpoints;
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
