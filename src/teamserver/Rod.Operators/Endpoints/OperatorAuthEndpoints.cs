using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Rod.CoreState;
using Rod.CoreState.Operators;
using Rod.Operators.Auth;

namespace Rod.Operators.Endpoints;

/// <summary>
/// The operator session and account endpoints (architecture.md Sec 4, the
/// production-hardening follow-on): <c>POST /operators/login</c> establishes a
/// cookie session from a handle and password, <c>POST /operators/logout</c>
/// clears it, and <c>GET /operators/me</c> returns the authenticated operator.
/// The account half provisions and administers the roster the bootstrap seed
/// used to be the only source of: <c>GET /operators</c> lists it,
/// <c>POST /operators</c> creates an account, and
/// <c>PUT /operators/{id}/credentials</c> re-provisions a password.
/// </summary>
public static class OperatorAuthEndpoints
{
    // The floor a provisioned password must clear. The login throttle blunts
    // online guessing but cannot replace a defensible minimum, and the dev
    // fallback account ("operator") already meets it.
    private const int MinimumPasswordLength = 8;

    public static IEndpointRouteBuilder Map(this IEndpointRouteBuilder endpoints)
    {
        // Login is anonymous (it is how a session is established); the roster
        // read, credential administration, and API-token management require
        // an existing session. Scope assignment, account provisioning, and
        // the credential re-provision additionally require the acting scope
        // (architecture.md Sec 4.5): a read-only operator cannot widen
        // themselves, an operator who can already act on every engagement is
        // not elevated by granting what they hold, and a provisioned account
        // or a reset password confers exactly that acting scope by default.
        endpoints.MapPost("/login", LoginAsync).AllowAnonymous();
        endpoints.MapGet("/", ListAsync).RequireAuthorization();
        endpoints.MapPost("/", CreateAsync).RequireAuthorization(OperatorScopes.TaskPolicy);
        endpoints.MapGet("/me", MeAsync).RequireAuthorization();
        endpoints.MapPost("/logout", LogoutAsync).RequireAuthorization();
        endpoints.MapPost("/{operatorId}/credentials:revoke", RevokeCredentialAsync).RequireAuthorization();
        endpoints.MapPut("/{operatorId}/credentials", SetCredentialAsync).RequireAuthorization(OperatorScopes.TaskPolicy);
        endpoints.MapPut("/{operatorId}/scopes", SetScopesAsync).RequireAuthorization(OperatorScopes.TaskPolicy);
        endpoints.MapPost("/{operatorId}/tokens", MintTokenAsync).RequireAuthorization();
        endpoints.MapGet("/{operatorId}/tokens", ListTokensAsync).RequireAuthorization();
        endpoints.MapPost("/{operatorId}/tokens/{tokenId}:revoke", RevokeTokenAsync).RequireAuthorization();
        return endpoints;
    }

    // Provisioning (architecture.md Sec 4.5 and Sec 9): the management path
    // the bootstrap seed stood in for. Creating an operator registers the
    // aggregate and stores the hash of its initial password in one step --
    // the same two calls the seed performs -- so the account is loginable
    // the moment the response returns. The route requires the acting scope
    // because a provisioned operator defaults to every scope: the caller
    // must hold what they grant, the line scope assignment draws. Omitted
    // scopes mean the peer default; an explicit empty array means none (the
    // account exists and can log in but holds nothing -- the parked-account
    // shape), the assignment route's own semantics. No audit event: like
    // every operator-account change it is global state and lands in no
    // engagement trail.
    private static async Task<IResult> CreateAsync(
        CreateOperatorRequest? body,
        IOperatorRepository operators,
        IOperatorCredentialStore credentials,
        IPasswordHasher<Operator> hasher,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body?.Handle))
            return Results.BadRequest(new Problem("Handle is required."));
        if (string.IsNullOrWhiteSpace(body.Password))
            return Results.BadRequest(new Problem("Password is required."));
        if (body.Password.Length < MinimumPasswordLength)
            return Results.BadRequest(new Problem(
                $"Password must be at least {MinimumPasswordLength} characters."));

        var handle = body.Handle.Trim();
        if (!TryParseScopes(body.Scopes, out var scopes, out var parseError))
            return Results.BadRequest(new Problem(parseError));
        if (body.Scopes is null)
            scopes = OperatorScope.All;

        var violation = OperatorScopes.Validate(scopes);
        if (violation is not null)
            return Results.Json(new Problem(violation), statusCode: StatusCodes.Status422UnprocessableEntity);

        if (await operators.FindByHandleAsync(handle, cancellationToken) is not null)
            return Results.Conflict(new Problem($"Handle '{handle}' is already taken."));

        var displayName = string.IsNullOrWhiteSpace(body.DisplayName) ? handle : body.DisplayName.Trim();
        var created = new Operator(OperatorId.New(), handle, displayName, clock.GetUtcNow(), scopes);
        await operators.SaveAsync(created, cancellationToken);
        await credentials.SetHashAsync(
            created.Id, hasher.HashPassword(created, body.Password), cancellationToken);
        return Results.Created($"/operators/{created.Id}", ToAccount(created, hasCredential: true));
    }

    // The roster: every account, ordered by handle, with whether a password
    // stands behind it (a revoked credential reads as absent). Any
    // authenticated operator may read it -- the trusted-operators stance the
    // token routes keep; the roster is account machinery like /me, and the
    // presence roster already names the colleagues on an engagement.
    private static async Task<IResult> ListAsync(
        IOperatorRepository operators,
        IOperatorCredentialStore credentials,
        CancellationToken cancellationToken)
    {
        var rows = await operators.ListAsync(cancellationToken);
        var accounts = new List<OperatorAccountResponse>(rows.Count);
        foreach (var op in rows)
        {
            var hash = await credentials.FindHashAsync(op.Id, cancellationToken);
            accounts.Add(ToAccount(op, hash is not null));
        }

        return Results.Ok(accounts);
    }

    // Re-provisions an operator's password (architecture.md Sec 9): the
    // restore twin of credential revocation, for the account whose verifier
    // was revoked or whose password must change. A new password is a new
    // credential generation -- the target's live cookie sessions fail their
    // stamp check at each one's next request, so the reset ends them without
    // touching anyone else's. Guarded by the acting scope like provisioning:
    // a reset confers the target's scopes on whoever receives the password.
    private static async Task<IResult> SetCredentialAsync(
        string operatorId,
        CredentialSetRequest? body,
        IOperatorRepository operators,
        IOperatorCredentialStore credentials,
        IPasswordHasher<Operator> hasher,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));
        if (string.IsNullOrWhiteSpace(body?.Password) || body.Password.Length < MinimumPasswordLength)
            return Results.BadRequest(new Problem(
                $"Password must be at least {MinimumPasswordLength} characters."));

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

        await credentials.SetHashAsync(target.Id, hasher.HashPassword(target, body.Password), cancellationToken);
        return Results.Ok(new { operatorId = target.Id.ToString() });
    }

    // Scope assignment (architecture.md Sec 4.5): the one guarded piece of
    // account machinery. The caller must hold the task scope (the route's
    // policy); the target's new set is validated (task and approve each
    // require read) and may not remove the last task holder -- a lockout
    // guard, not a security boundary. Like every operator-account change it
    // is global state, so it lands in no engagement trail; the per-request
    // session validation delivers the new set to the target's live cookie at
    // its next request.
    private static async Task<IResult> SetScopesAsync(
        string operatorId,
        ScopeAssignmentRequest? body,
        IOperatorRepository operators,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new { message = "Operator id is not a valid identifier." });

        if (!TryParseScopes(body?.Scopes, out var scopes, out var parseError))
            return Results.BadRequest(new { message = parseError });

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new { message = $"Operator {operatorId} does not exist." });

        var violation = OperatorScopes.Validate(scopes);
        if (violation is not null)
            return Results.Json(new { message = violation }, statusCode: StatusCodes.Status422UnprocessableEntity);

        // The lockout guard: removing the target's task scope must leave
        // another task holder standing.
        if (target.Scopes.HasFlag(OperatorScope.Task) && !scopes.HasFlag(OperatorScope.Task))
        {
            var others = (await operators.ListAsync(cancellationToken))
                .Where(o => o.Id != target.Id && o.Scopes.HasFlag(OperatorScope.Task))
                .ToList();
            if (others.Count == 0)
                return Results.Conflict(new
                {
                    message = "Refusing to remove the last task scope: another operator must hold it first.",
                });
        }

        var updated = target.WithScopes(scopes);
        await operators.SaveAsync(updated, cancellationToken);
        return Results.Ok(ToSummary(updated));
    }

    // Parses the request's scope names. Unknown names are refused here -- an
    // assignment naming a scope this server does not know is a client error,
    // unlike the claim-value parser's degrade posture.
    private static bool TryParseScopes(string[]? names, out OperatorScope scopes, out string error)
    {
        scopes = OperatorScope.None;
        if (names is null || names.Length == 0)
        {
            // An empty assignment is valid: the operator is locked out of the
            // engagement surface entirely (login and account reads remain).
            error = string.Empty;
            return true;
        }

        foreach (var name in names)
        {
            switch (name?.Trim().ToLowerInvariant())
            {
                case "read":
                    scopes |= OperatorScope.Read;
                    break;
                case "task":
                    scopes |= OperatorScope.Task;
                    break;
                case "approve":
                    scopes |= OperatorScope.Approve;
                    break;
                default:
                    error = $"Unknown scope '{name}'.";
                    return false;
            }
        }

        error = string.Empty;
        return true;
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
        => new(op.Id.Value, op.Handle, op.DisplayName, OperatorScopes.ToClaimValue(op.Scopes)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static OperatorAccountResponse ToAccount(Operator op, bool hasCredential)
        => new(op.Id.Value, op.Handle, op.DisplayName, OperatorScopes.ToClaimValue(op.Scopes)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            op.CreatedAt, hasCredential);
}

/// <summary>Login credentials submitted to <c>POST /operators/login</c>.</summary>
public sealed record LoginRequest(string Handle, string Password);

/// <summary>
/// The scope assignment submitted to <c>PUT /operators/{id}/scopes</c>: the
/// complete new set by name (an empty assignment locks the operator out of
/// the engagement surface).
/// </summary>
public sealed record ScopeAssignmentRequest(string[]? Scopes);

/// <summary>
/// A new account submitted to <c>POST /operators</c>: handle and initial
/// password are required; the display name defaults to the handle, and the
/// scope set to the peer default when omitted (an explicit empty array
/// provisions the account with no scopes at all).
/// </summary>
public sealed record CreateOperatorRequest(string? Handle, string? DisplayName, string? Password, string[]? Scopes);

/// <summary>The replacement password submitted to <c>PUT /operators/{id}/credentials</c>.</summary>
public sealed record CredentialSetRequest(string? Password);

/// <summary>
/// A roster row as <c>GET /operators</c> returns it: the account's identity,
/// scope set, creation time, and whether a password currently stands behind
/// it (false after credential revocation -- the account exists but cannot
/// log in until re-provisioned).
/// </summary>
public sealed record OperatorAccountResponse(
    Guid Id,
    string Handle,
    string DisplayName,
    string[] Scopes,
    DateTimeOffset CreatedAt,
    bool HasCredential);

/// <summary>
/// The authenticated operator returned by login and <c>GET /operators/me</c>:
/// identity and the scope set the session carries.
/// </summary>
public sealed record OperatorAuthSummary(Guid Id, string Handle, string DisplayName, string[] Scopes);

/// <summary>
/// A freshly minted API token: the secret is shown exactly once, here -- only
/// its digest is stored afterwards.
/// </summary>
public sealed record MintedTokenResponse(string TokenId, string Token, DateTimeOffset CreatedAt);

/// <summary>A minted API token as a listing row (identity and lifetime, never the secret).</summary>
public sealed record TokenResponse(string TokenId, DateTimeOffset CreatedAt);
