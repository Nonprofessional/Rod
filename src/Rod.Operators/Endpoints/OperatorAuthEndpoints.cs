using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState.Operators;
using Rod.Operators.Auth;

namespace Rod.Operators.Endpoints;

/// <summary>
/// The operator session endpoints (architecture.md Sec 4, the production-
/// hardening follow-on): <c>POST /operators/login</c> establishes a cookie
/// session from a handle and password, <c>POST /operators/logout</c> clears it,
/// and <c>GET /operators/me</c> returns the authenticated operator. This replaces
/// the walking skeleton's browser self-assigned identity with a server-issued
/// session bound to verified credentials.
/// </summary>
public static class OperatorAuthEndpoints
{
    public static IEndpointRouteBuilder Map(this IEndpointRouteBuilder endpoints)
    {
        // Login is anonymous (it is how a session is established); logout and the
        // current-operator read require an existing session.
        endpoints.MapPost("/login", LoginAsync).AllowAnonymous();
        endpoints.MapPost("/logout", LogoutAsync).RequireAuthorization();
        endpoints.MapGet("/me", MeAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest body,
        OperatorAuthService auth,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body?.Handle) || string.IsNullOrWhiteSpace(body?.Password))
            return Results.BadRequest(new { message = "Handle and password are required." });

        var result = await auth.TryLoginAsync(body.Handle, body.Password, cancellationToken);
        if (!result.Success || result.Principal is null || result.Operator is null)
            return Results.Unauthorized();

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
