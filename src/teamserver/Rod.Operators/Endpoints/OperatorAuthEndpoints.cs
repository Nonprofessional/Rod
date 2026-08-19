using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Rod.CoreState;
using Rod.CoreState.Operators;
using Rod.Operators.Auth;

namespace Rod.Operators.Endpoints;

/// <summary>
/// The operator session endpoints (architecture.md Sec 4, the production-
/// hardening follow-on): <c>POST /operators/login</c> establishes a cookie
/// session from a handle and password, <c>POST /operators/logout</c> clears it,
/// and <c>GET /operators/me</c> returns the authenticated operator. This replaces
/// an anonymous self-assigned identity with a server-issued
/// session bound to verified credentials.
/// </summary>
public static class OperatorAuthEndpoints
{
    public static IEndpointRouteBuilder Map(this IEndpointRouteBuilder endpoints)
    {
        // Login is anonymous (it is how a session is established); logout, the
        // current-operator read, credential revocation, and API-token
        // management require an existing session.
        endpoints.MapPost("/login", LoginAsync).AllowAnonymous();
        endpoints.MapPost("/logout", LogoutAsync).RequireAuthorization();
        endpoints.MapGet("/me", MeAsync).RequireAuthorization();
        endpoints.MapPost("/{operatorId}/credentials:revoke", RevokeCredentialAsync).RequireAuthorization();
        endpoints.MapPost("/{operatorId}/tokens", MintTokenAsync).RequireAuthorization();
        endpoints.MapGet("/{operatorId}/tokens", ListTokensAsync).RequireAuthorization();
        endpoints.MapPost("/{operatorId}/tokens/{tokenId}:revoke", RevokeTokenAsync).RequireAuthorization();
        return endpoints;
    }

    // API-token management (architecture.md Sec 9 -- the identity model's API
    // tokens): a bearer credential minted per operator, honored by the
    // operator API alongside cookie sessions, and revocable like credentials.
    // The plaintext secret is shown exactly once, at mint; only its digest is
    // stored. Any authenticated operator may manage tokens (the
    // trusted-operators model); revocation is idempotent. A token is
    // independent of the password credential -- each revokes through its own
    // route -- so rotating one never silently invalidates the other.
    private static async Task<IResult> MintTokenAsync(
        string operatorId,
        IOperatorRepository operators,
        IOperatorApiTokenStore tokens,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new { message = "Operator id is not a valid identifier." });

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new { message = $"Operator {operatorId} does not exist." });

        var minted = await tokens.MintAsync(target.Id, clock.GetUtcNow(), cancellationToken);
        return Results.Ok(new MintedTokenResponse(
            minted.TokenId.ToString(),
            minted.Secret,
            minted.CreatedAt));
    }

    private static async Task<IResult> ListTokensAsync(
        string operatorId,
        IOperatorRepository operators,
        IOperatorApiTokenStore tokens,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new { message = "Operator id is not a valid identifier." });

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new { message = $"Operator {operatorId} does not exist." });

        var rows = await tokens.ListAsync(target.Id, cancellationToken);
        return Results.Ok(rows.Select(r => new TokenResponse(r.TokenId.ToString(), r.CreatedAt)));
    }

    private static async Task<IResult> RevokeTokenAsync(
        string operatorId,
        string tokenId,
        IOperatorRepository operators,
        IOperatorApiTokenStore tokens,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new { message = "Operator id is not a valid identifier." });
        if (!Guid.TryParse(tokenId, out var tokenValue))
            return Results.BadRequest(new { message = "Token id is not a valid identifier." });

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new { message = $"Operator {operatorId} does not exist." });

        // Idempotent: revoking an unknown token succeeds, like credential
        // revocation. The next request presenting it fails -- the digest is
        // read fresh on every attempt.
        await tokens.RevokeAsync(target.Id, new OperatorApiTokenId(tokenValue), cancellationToken);
        return Results.Ok(new { operatorId = target.Id.ToString(), tokenId });
    }

    // Revocation is the operator half of certificate revocation
    // (architecture.md Sec 9): deleting the stored verifier makes the next
    // login fail -- the hash is read fresh per attempt, so no restart is
    // involved. It ends the credential's live cookie sessions too: each
    // authenticated request revalidates the session stamp the login baked
    // into the cookie, and with the verifier gone the stamp no longer
    // matches, so the next request on that cookie is refused. Any
    // authenticated operator may revoke (the trusted-operators model); the
    // action is idempotent.
    private static async Task<IResult> RevokeCredentialAsync(
        string operatorId,
        IOperatorRepository operators,
        IOperatorCredentialStore credentials,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new { message = "Operator id is not a valid identifier." });

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new { message = $"Operator {operatorId} does not exist." });

        await credentials.RevokeAsync(target.Id, cancellationToken);
        return Results.Ok(new { operatorId = target.Id.ToString() });
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest body,
        OperatorAuthService auth,
        LoginThrottle throttle,
        ILoggerFactory loggerFactory,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body?.Handle) || string.IsNullOrWhiteSpace(body?.Password))
            return Results.BadRequest(new { message = "Handle and password are required." });

        var handle = body.Handle.Trim();
        var logger = loggerFactory.CreateLogger("Rod.Operators.Endpoints.OperatorAuthEndpoints");
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // The account is the only boundary between an attacker and the
        // teamserver (no per-engagement RBAC by design), so repeated failures
        // put the handle into a cooldown instead of allowing unbounded online
        // brute force (architecture.md Sec 9).
        if (!throttle.IsAllowed(handle))
        {
            logger.LogWarning("Login for handle {Handle} from {Remote} refused: cooldown active.", handle, remote);
            return Results.Json(new { message = "Too many failed attempts; try again later." },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var result = await auth.TryLoginAsync(handle, body.Password, cancellationToken);
        if (!result.Success || result.Principal is null || result.Operator is null)
        {
            throttle.RecordFailure(handle);
            logger.LogWarning("Login for handle {Handle} from {Remote} failed.", handle, remote);
            return Results.Unauthorized();
        }

        throttle.Reset(handle);
        logger.LogInformation("Operator {Handle} logged in from {Remote}.", handle, remote);
        await context.SignInAsync(
            OperatorAuthConstants.AuthenticationScheme,
            result.Principal);
        return Results.Ok(ToSummary(result.Operator));
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, CancellationToken cancellationToken)
    {
        await context.SignOutAsync(OperatorAuthConstants.AuthenticationScheme);
        return Results.Ok(new { status = "ok" });
    }

    private static async Task<IResult> MeAsync(
        ClaimsPrincipal user,
        IOperatorRepository operators,
        CancellationToken cancellationToken)
    {
        var opId = user.TryGetOperatorId();
        if (opId is null)
            return Results.Unauthorized();

        var op = await operators.FindAsync(opId.Value, cancellationToken);
        if (op is null)
            return Results.Unauthorized();

        return Results.Ok(ToSummary(op));
    }

    private static OperatorAuthSummary ToSummary(Operator op)
        => new(op.Id.Value, op.Handle, op.DisplayName);
}

/// <summary>Login credentials submitted to <c>POST /operators/login</c>.</summary>
public sealed record LoginRequest(string Handle, string Password);

/// <summary>The authenticated operator returned by login and <c>GET /operators/me</c>.</summary>
public sealed record OperatorAuthSummary(Guid Id, string Handle, string DisplayName);

/// <summary>
/// A freshly minted API token: the secret is shown exactly once, here -- only
/// its digest is stored afterwards.
/// </summary>
public sealed record MintedTokenResponse(string TokenId, string Token, DateTimeOffset CreatedAt);

/// <summary>A minted API token as a listing row (identity and lifetime, never the secret).</summary>
public sealed record TokenResponse(string TokenId, DateTimeOffset CreatedAt);
