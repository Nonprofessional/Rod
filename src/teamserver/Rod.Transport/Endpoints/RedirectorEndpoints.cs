using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Operators;
using Rod.CoreState.Redirectors;
using Rod.Transport.Redirectors;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The redirector fleet's operator surface (architecture.md Sec 8):
/// deployment-scoped, like the fleet it manages -- no engagement in the
/// route, because a node is machine capacity and engagement scope arrives
/// per front at the listener it attaches to. Authentication is the trusted-
/// operator model every non-engagement route uses (the credential-revocation
/// shape): any authenticated operator mints and retires, and the actions are
/// audited where they touch an engagement (front changes) or not at all
/// (fleet plumbing, matching the operator-identity family's posture).
/// </summary>
public static class RedirectorEndpoints
{
    public static IEndpointRouteBuilder MapRedirectorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/redirectors").RequireAuthorization();

        group.MapGet("/", ListNodesAsync).WithName(nameof(ListNodesAsync));
        group.MapGet("/credentials", ListCredentialsAsync).WithName(nameof(ListCredentialsAsync));
        group.MapPost("/credentials", MintCredentialAsync).WithName(nameof(MintCredentialAsync));
        group.MapPost("/credentials/{id}:revoke", RevokeCredentialAsync).WithName(nameof(RevokeCredentialAsync));
        group.MapPost("/{id}:retire", RetireNodeAsync).WithName(nameof(RetireNodeAsync));

        return endpoints;
    }

    private static async Task<IResult> ListNodesAsync(
        IRedirectorFleet fleet,
        RedirectorControlHub hub,
        CancellationToken cancellationToken)
    {
        var nodes = (await fleet.ListNodesAsync(cancellationToken))
            .Select(n => NodeResponse.Of(n, hub.IsOnline(n.Id)))
            .ToArray();
        return Results.Ok(nodes);
    }

    private static async Task<IResult> ListCredentialsAsync(
        IRedirectorFleet fleet,
        CancellationToken cancellationToken)
        => Results.Ok((await fleet.ListCredentialsAsync(cancellationToken)).Select(CredentialResponse.Of).ToArray());

    private static async Task<IResult> MintCredentialAsync(
        MintCredentialRequest body,
        System.Security.Claims.ClaimsPrincipal user,
        IRedirectorFleet fleet,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new Problem("Credential name is required -- it becomes the enrolled node's roster name."));
        if (body.Name.Trim().Length > 64)
            return Results.BadRequest(new Problem("Credential name must be 64 characters or fewer."));
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        var minted = await fleet.MintCredentialAsync(
            body.Name.Trim(),
            operatorId.Value,
            DateTimeOffset.UtcNow,
            body.TtlHours is { } hours ? TimeSpan.FromHours(hours) : null,
            cancellationToken);

        // The secret exists exactly once, here: the response is the only
        // place it is ever readable (the ledger keeps hashes and stamps).
        return Results.Created(
            $"/redirectors/credentials/{minted.Id}",
            new MintedCredentialResponse(
                minted.Id.ToString(),
                minted.Name,
                minted.Secret,
                minted.IssuedAt,
                minted.ExpiresAt));
    }

    private static async Task<IResult> RevokeCredentialAsync(
        string id,
        IRedirectorFleet fleet,
        CancellationToken cancellationToken)
    {
        if (!RedirectorCredentialId.TryParse(id, out var credentialId))
            return Results.BadRequest(new Problem("Credential id is not a valid identifier."));

        // Idempotent, and a no-op on an unknown id: revoking a credential
        // that was never minted leaves nothing to enforce. Revoking one
        // that already enrolled changes nothing for its node -- retire the
        // node for that.
        await fleet.RevokeCredentialAsync(credentialId, DateTimeOffset.UtcNow, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RetireNodeAsync(
        string id,
        IRedirectorFleet fleet,
        RedirectorControlHub hub,
        CancellationToken cancellationToken)
    {
        if (!RedirectorNodeId.TryParse(id, out var nodeId))
            return Results.BadRequest(new Problem("Node id is not a valid identifier."));

        var node = await fleet.FindAsync(nodeId, cancellationToken);
        if (node is null)
            return Results.NotFound(new Problem($"Redirector node {id} is not enrolled."));

        await fleet.RetireAsync(nodeId, DateTimeOffset.UtcNow, cancellationToken);
        // The live session goes with the retirement: the node's next hello
        // is refused by name, and nothing pushes rules to it in between.
        await hub.CloseAsync(nodeId, cancellationToken);
        return Results.NoContent();
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record MintCredentialRequest(
        string Name,
        int? TtlHours = null);

    public sealed record MintedCredentialResponse(
        string Id,
        string Name,
        string Secret,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt);

    public sealed record CredentialResponse(
        string Id,
        string Name,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        string? EnrolledNodeId,
        DateTimeOffset? RevokedAt)
    {
        public static CredentialResponse Of(RedirectorCredentialState c)
            => new(c.Id.ToString(), c.Name, c.IssuedAt, c.ExpiresAt,
                c.EnrolledNodeId?.ToString(), c.RevokedAt);
    }

    public sealed record NodeResponse(
        string Id,
        string Name,
        bool Online,
        DateTimeOffset EnrolledAt,
        DateTimeOffset? RetiredAt,
        DateTimeOffset LastSeenAt,
        string? LastAddress,
        string? ReportedVersion,
        string? ReportedCommit)
    {
        public static NodeResponse Of(RedirectorNode n, bool online)
            => new(
                n.Id.ToString(),
                n.Name,
                online,
                n.EnrolledAt,
                n.RetiredAt,
                n.LastSeenAt,
                n.LastAddress,
                n.ReportedVersion,
                n.ReportedCommit);
    }
}
