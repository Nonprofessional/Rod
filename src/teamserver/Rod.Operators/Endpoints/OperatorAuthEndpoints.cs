using System.Security.Claims;
using Rod.CoreState;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Rod.CoreState.Operators;
using Rod.Operators.Auth;

namespace Rod.Operators.Endpoints;

/// <summary>
/// The operator session and account endpoints (architecture.md Sec 3 and
/// Sec 9): <c>POST /operators/login</c> establishes a cookie session from a
/// handle and password, <c>POST /operators/logout</c> clears it, and
/// <c>GET /operators/me</c> returns the authenticated operator. The account
/// half provisions and administers the roster the bootstrap seed used to be
/// the only source of: <c>GET /operators</c> lists it,
/// <c>POST /operators</c> creates an account,
/// <c>PUT /operators/{id}/credentials</c> re-provisions a password, and
/// <c>POST /operators/{id}:disable</c> / <c>:enable</c> are the account's
/// off and back on. An account carries no permission -- what an operator may
/// reach lives per engagement, as the memberships its owners granted -- so
/// the account machinery is trusted-operator throughout (the stance the
/// token routes always kept): any authenticated operator may provision,
/// administer, and disable, held to the audit-free global account surface
/// they already answer for.
/// </summary>
public static class OperatorAuthEndpoints
{
    // The floor a provisioned password must clear. The login throttle blunts
    // online guessing but cannot replace a defensible minimum, and the dev
    // fallback account ("operator") already meets it.
    private const int MinimumPasswordLength = 8;

    public static IEndpointRouteBuilder Map(this IEndpointRouteBuilder endpoints)
    {
        // Login is anonymous (it is how a session is established); everything
        // else requires an existing session, and nothing more -- accounts are
        // identity, not permission.
        endpoints.MapPost("/login", LoginAsync).AllowAnonymous();
        endpoints.MapGet("/", ListAsync).RequireAuthorization();
        endpoints.MapPost("/", CreateAsync).RequireAuthorization();
        endpoints.MapGet("/me", MeAsync).RequireAuthorization();
        endpoints.MapPost("/logout", LogoutAsync).RequireAuthorization();
        endpoints.MapPost("/{operatorId}/credentials:revoke", RevokeCredentialAsync).RequireAuthorization();
        endpoints.MapPut("/{operatorId}/credentials", SetCredentialAsync).RequireAuthorization();
        endpoints.MapPost("/{operatorId}:disable", DisableAsync).RequireAuthorization();
        endpoints.MapPost("/{operatorId}:enable", EnableAsync).RequireAuthorization();
        endpoints.MapPost("/{operatorId}/tokens", MintTokenAsync).RequireAuthorization();
        endpoints.MapGet("/{operatorId}/tokens", ListTokensAsync).RequireAuthorization();
        endpoints.MapPost("/{operatorId}/tokens/{tokenId}:revoke", RevokeTokenAsync).RequireAuthorization();
        return endpoints;
    }

    // Provisioning (architecture.md Sec 3 and Sec 9): the management path the
    // bootstrap seed stood in for. Creating an operator registers the
    // aggregate and stores the hash of its initial password in one step --
    // the same two calls the seed performs -- so the account is loginable
    // the moment the response returns. The account carries no permission:
    // what it may reach arrives later, as engagement memberships its owners
    // grant. No audit event: account changes are global state and land in no
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
        if (await operators.FindByHandleAsync(handle, cancellationToken) is not null)
            return Results.Conflict(new Problem($"Handle '{handle}' is already taken."));

        var displayName = string.IsNullOrWhiteSpace(body.DisplayName) ? handle : body.DisplayName.Trim();
        var created = new Operator(OperatorId.New(), handle, displayName, clock.GetUtcNow());
        await operators.SaveAsync(created, cancellationToken);
        await credentials.SetHashAsync(
            created.Id, hasher.HashPassword(created, body.Password), cancellationToken);
        return Results.Created($"/operators/{created.Id}", ToAccount(created, hasCredential: true));
    }

    // The roster: every account, ordered by handle, with whether a password
    // stands behind it (a revoked credential reads as absent). Any
    // authenticated operator may read it -- the trusted-operators stance the
    // token routes keep; the roster is account machinery like /me.
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
    // touching anyone else's.
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

    // The administrative off switch: a disabled operator authenticates
    // nowhere -- login fails indistinguishably, live cookie sessions reject
    // at their next request, and API tokens refuse -- until enabled again.
    // The engagements a disabled operator could reach are unaffected as
    // records (their memberships wait), so an enable restores exactly the
    // reach the account had. This is the honest "retire a colleague" shape:
    // the row stays, anchoring every attribution it ever collected.
    // Idempotent, and guarded only against the lockout -- disabling the last
    // enabled account would leave nobody able to enable it back.
    private static async Task<IResult> DisableAsync(
        string operatorId,
        IOperatorRepository operators,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

        // The lockout guard: some other enabled account must remain standing.
        // The automation row holds no credential and never counts.
        if (!target.Disabled)
        {
            var others = (await operators.ListAsync(cancellationToken))
                .Any(o => o.Id != target.Id && !o.Disabled
                    && o.Id != Automation.AutomationOperatorIdentity.OperatorId);
            if (!others)
                return Results.Conflict(new Problem(
                    "Refusing to disable the last enabled operator: another account must remain able to enable it back."));
        }

        if (target.Disabled)
            return Results.Ok(new { operatorId = target.Id.ToString(), disabled = true });

        var updated = target.WithDisabled(true);
        await operators.SaveAsync(updated, cancellationToken);
        return Results.Ok(new { operatorId = updated.Id.ToString(), disabled = true });
    }

    // The restore twin: enabling re-opens every authentication path for the
    // account with the memberships it kept through the disable. No guard
    // applies -- enabling widens nothing an account did not already hold,
    // and nobody locks themselves out by enabling someone.
    private static async Task<IResult> EnableAsync(
        string operatorId,
        IOperatorRepository operators,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

        if (!target.Disabled)
            return Results.Ok(new { operatorId = target.Id.ToString(), disabled = false });

        var updated = target.WithDisabled(false);
        await operators.SaveAsync(updated, cancellationToken);
        return Results.Ok(new { operatorId = updated.Id.ToString(), disabled = false });
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
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

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
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

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
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));
        if (!Guid.TryParse(tokenId, out var tokenValue))
            return Results.BadRequest(new Problem("Token id is not a valid identifier."));

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

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
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

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
            return Results.BadRequest(new Problem("Handle and password are required."));

        var handle = body.Handle.Trim();
        var logger = loggerFactory.CreateLogger("Rod.Operators.Endpoints.OperatorAuthEndpoints");
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // The account is the only boundary between an attacker and the
        // teamserver, so repeated failures put the handle into a cooldown
        // instead of allowing unbounded online brute force (Sec 9).
        if (!throttle.IsAllowed(handle))
        {
            logger.LogWarning("Login for handle {Handle} from {Remote} refused: cooldown active.", handle, remote);
            return Results.Json(new Problem("Too many failed attempts; try again later."),
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

    private static OperatorAccountResponse ToAccount(Operator op, bool hasCredential)
        => new(op.Id.Value, op.Handle, op.DisplayName, op.CreatedAt, hasCredential, op.Disabled);
}

/// <summary>Login credentials submitted to <c>POST /operators/login</c>.</summary>
public sealed record LoginRequest(string Handle, string Password);

/// <summary>
/// A new account submitted to <c>POST /operators</c>: handle and initial
/// password are required; the display name defaults to the handle. The
/// account carries no permission -- reach arrives per engagement, as
/// memberships its owners grant.
/// </summary>
public sealed record CreateOperatorRequest(string? Handle, string? DisplayName, string? Password);

/// <summary>The replacement password submitted to <c>PUT /operators/{id}/credentials</c>.</summary>
public sealed record CredentialSetRequest(string? Password);

/// <summary>
/// The authenticated operator returned by login and <c>GET /operators/me</c>:
/// identity only -- what the operator may reach resolves per engagement.
/// </summary>
public sealed record OperatorAuthSummary(Guid Id, string Handle, string DisplayName);

/// <summary>
/// A freshly minted API token: the secret is shown exactly once, here -- only
/// its digest is stored afterwards.
/// </summary>
public sealed record MintedTokenResponse(string TokenId, string Token, DateTimeOffset CreatedAt);

/// <summary>A minted API token as a listing row (identity and lifetime, never the secret).</summary>
public sealed record TokenResponse(string TokenId, DateTimeOffset CreatedAt);

/// <summary>
/// A roster row as <c>GET /operators</c> returns it: the account's identity,
/// creation time, whether a password currently stands behind it (false after
/// credential revocation -- the account exists but cannot log in until
/// re-provisioned), and whether the account is disabled (no authentication
/// path accepts it until enabled).
/// </summary>
public sealed record OperatorAccountResponse(
    Guid Id,
    string Handle,
    string DisplayName,
    DateTimeOffset CreatedAt,
    bool HasCredential,
    bool Disabled);
