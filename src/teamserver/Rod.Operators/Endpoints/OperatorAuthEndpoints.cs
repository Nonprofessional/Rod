using System.Security.Claims;
using Rod.CoreState;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Rod.Audit;
using Rod.CoreState.Operators;
using Rod.Operators.Auth;

namespace Rod.Operators.Endpoints;

/// <summary>
/// The operator session and account endpoints (architecture.md Sec 3 and
/// Sec 9): <c>POST /operators/login</c> establishes a cookie session from a
/// handle and password, <c>POST /operators/logout</c> clears it, and
/// <c>GET /operators/me</c> returns the authenticated operator. The account
/// half splits along the deployment roles (Sec 4.5): the bootstrap-seeded
/// admin provisions (<c>POST /operators</c>), re-provisions passwords
/// (<c>PUT /operators/{id}/credentials</c>), holds the disable pair
/// (<c>POST /operators/{id}:disable</c> / <c>:enable</c>), and manages API
/// tokens on any account, while every operator keeps self-service over its
/// own password and tokens. The roster read (<c>GET /operators</c>) is the
/// admin's alone: a member's account surface is <c>GET /operators/me</c>,
/// and the handles it works beside arrive per engagement, from that
/// engagement's member roster -- never the deployment-wide account list.
/// </summary>
public static class OperatorAuthEndpoints
{
    // The floor a console-set password must clear. The login throttle
    // blunts online guessing but cannot replace a defensible minimum. The
    // bootstrap seed stands outside it: configuration is the deployment's
    // own act, not console input.
    private const int MinimumPasswordLength = 8;

    public static IEndpointRouteBuilder Map(this IEndpointRouteBuilder endpoints)
    {
        // Login is anonymous (it is how a session is established); everything
        // else requires an existing session. Provisioning and the roster
        // read carry the admin claim requirement directly; the account
        // routes that take an operator id gate in the handler, where "self
        // or admin" is expressible (a claim requirement cannot say "self").
        endpoints.MapPost("/login", LoginAsync).AllowAnonymous();
        endpoints.MapGet("/", ListAsync).RequireAuthorization(DeploymentAdmin());
        endpoints.MapPost("/", CreateAsync).RequireAuthorization(DeploymentAdmin());
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

    // The authorization requirement the admin-only account route carries:
    // the deployment-role claim (Sec 4.5). It composes with the group's plain
    // requirement -- a member is authenticated and still forbidden (403), an
    // anonymous request is challenged (401).
    private static Action<AuthorizationPolicyBuilder> DeploymentAdmin()
        => policy => policy.RequireClaim(
            OperatorClaims.DeploymentRoleClaimType, DeploymentRoles.AdminName);

    // The self-or-admin gate the account routes share: an account's password
    // and tokens are its own to manage, and acting on another operator's
    // account is the admin's alone. The refusal is by role, not existence --
    // it names the route's rule, never whether an account stands behind the
    // id.
    private static bool MayActOn(ClaimsPrincipal user, OperatorId target)
        => user.TryGetOperatorId() == target || user.IsDeploymentAdmin();

    // The deployment bucket: account and credential changes are global
    // state with no engagement to live in, and the trail is the only
    // attributed record the platform keeps -- the server log is not one.
    // Guid.Empty is the bucket's id (the same "none" convention the event's
    // own implant and task ids already use), its chain verified and read
    // like any engagement's, through the admin-only deployment trail route.
    private static async Task RecordAccountChangeAsync(
        IAuditStore audit,
        OperatorId actor,
        OperatorId target,
        string verb,
        string payload,
        TimeProvider clock,
        CancellationToken cancellationToken)
        => await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: Guid.Empty,
                operatorId: actor.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: verb,
                kind: AuditEventKind.OperatorAccountChanged,
                payload: payload,
                output: null,
                outcome: target.Value.ToString(),
                at: clock.GetUtcNow()),
            cancellationToken);

    // Provisioning (architecture.md Sec 3 and Sec 9): the management path the
    // bootstrap seed stood in for, and the admin's alone -- every account it
    // creates is a member, the role fixed at registration. Creating an
    // operator registers the aggregate and stores the hash of its initial
    // password in one step -- the same two calls the seed performs -- so the
    // account is loginable the moment the response returns. What it may
    // reach arrives later, as engagement memberships its owners grant. No
    // audit event: account changes are global state and land in no
    // engagement trail.
    private static async Task<IResult> CreateAsync(
        CreateOperatorRequest? body,
        ClaimsPrincipal user,
        IOperatorRepository operators,
        IOperatorCredentialStore credentials,
        IPasswordHasher<Operator> hasher,
        IAuditStore audit,
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

        var created = new Operator(
            OperatorId.New(), handle, clock.GetUtcNow(),
            deploymentRole: DeploymentRole.Member);
        await operators.SaveAsync(created, cancellationToken);
        await credentials.SetHashAsync(
            created.Id, hasher.HashPassword(created, body.Password), cancellationToken);
        await RecordAccountChangeAsync(audit, user.TryGetOperatorId()!.Value, created.Id,
            "operator.created", $"handle='{created.Handle}' role=member", clock, cancellationToken);
        return Results.Created($"/operators/{created.Id}", ToAccount(created, hasCredential: true));
    }

    // The roster: every account, ordered by handle, with whether a password
    // stands behind it (a revoked credential reads as absent). The
    // deployment admin's read -- a member's account surface is
    // /operators/me, and the handles it works beside arrive per engagement,
    // from that engagement's member roster, so no surface needs the
    // deployment-wide list. Each row carries its deployment role -- the one
    // privilege an account holds.
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
    // was revoked or whose password must change. Self-service on the
    // caller's own account, the admin's alone on anyone else's -- a password
    // reset on another operator is account takeover, which is management.
    // Acting on your own account proves the current password: a session
    // alone must not rotate the credential behind it, or a stolen cookie
    // would quietly own the account forever. An admin acting on another
    // account states no proof -- takeover is the management path's
    // prerogative, not this route's concern. A new password is a new
    // credential generation -- the target's live cookie sessions fail their
    // stamp check at each one's next request, so the reset ends them
    // without touching anyone else's.
    private static async Task<IResult> SetCredentialAsync(
        string operatorId,
        ClaimsPrincipal user,
        CredentialSetRequest? body,
        IOperatorRepository operators,
        IOperatorCredentialStore credentials,
        IPasswordHasher<Operator> hasher,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));
        if (!MayActOn(user, new OperatorId(operatorValue)))
            return Results.Forbid();
        if (string.IsNullOrWhiteSpace(body?.Password) || body.Password.Length < MinimumPasswordLength)
            return Results.BadRequest(new Problem(
                $"Password must be at least {MinimumPasswordLength} characters."));

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

        var self = user.TryGetOperatorId() == target.Id;
        if (self)
        {
            var hash = await credentials.FindHashAsync(target.Id, cancellationToken);
            var verification = hash is null
                ? PasswordVerificationResult.Failed
                : hasher.VerifyHashedPassword(target, hash, body.CurrentPassword ?? string.Empty);
            if (verification == PasswordVerificationResult.Failed)
                return Results.Json(new Problem("The current password does not verify."),
                    statusCode: StatusCodes.Status403Forbidden);
        }

        await credentials.SetHashAsync(target.Id, hasher.HashPassword(target, body.Password), cancellationToken);
        await RecordAccountChangeAsync(audit, user.TryGetOperatorId()!.Value, target.Id,
            "operator.credential-set",
            self
                ? $"password changed on '{target.Handle}' (self-service; a new credential generation: its live sessions end)"
                : $"password set on '{target.Handle}' by the admin (a new credential generation: its live sessions end)",
            clock, cancellationToken);
        return Results.Ok(new { operatorId = target.Id.ToString() });
    }

    // The administrative off switch: a disabled operator authenticates
    // nowhere -- login fails indistinguishably, live cookie sessions reject
    // at their next request, and API tokens refuse -- until enabled again.
    // The engagements a disabled operator could reach are unaffected as
    // records (their memberships wait), so an enable restores exactly the
    // reach the account had. This is the honest "retire a colleague" shape:
    // the row stays, anchoring every attribution it ever collected. The
    // pair is the admin's alone, even on the admin's own account --
    // self-service covers password and tokens, not the off switch.
    // Idempotent, and guarded against the two lockouts: disabling the last
    // enabled account would leave nobody able to enable it back, and
    // disabling the last enabled admin would leave the deployment with no
    // management path (there is no promote surface to grow a new one --
    // only the bootstrap seed can, at boot).
    private static async Task<IResult> DisableAsync(
        string operatorId,
        ClaimsPrincipal user,
        IOperatorRepository operators,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));
        if (!user.IsDeploymentAdmin())
            return Results.Forbid();

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

        if (!target.Disabled)
        {
            // The automation row holds no credential and never counts: it
            // can neither enable anyone back nor stand in as an admin.
            var roster = await operators.ListAsync(cancellationToken);
            if (!roster.Any(o => o.Id != target.Id && !o.Disabled
                    && o.Id != Automation.AutomationOperatorIdentity.OperatorId))
                return Results.Conflict(new Problem(
                    "Refusing to disable the last enabled operator: another account must remain able to enable it back."));
            if (target.DeploymentRole == DeploymentRole.Admin
                && !roster.Any(o => o.Id != target.Id && !o.Disabled
                    && o.DeploymentRole == DeploymentRole.Admin
                    && o.Id != Automation.AutomationOperatorIdentity.OperatorId))
                return Results.Conflict(new Problem(
                    "Refusing to disable the last enabled admin: another admin must remain able to manage the deployment."));
        }

        if (target.Disabled)
            return Results.Ok(new { operatorId = target.Id.ToString(), disabled = true });

        var updated = target.WithDisabled(true);
        await operators.SaveAsync(updated, cancellationToken);
        await RecordAccountChangeAsync(audit, user.TryGetOperatorId()!.Value, updated.Id,
            "operator.disabled", $"disabled '{updated.Handle}' (authenticates nowhere until enabled)", clock, cancellationToken);
        return Results.Ok(new { operatorId = updated.Id.ToString(), disabled = true });
    }

    // The restore twin: enabling re-opens every authentication path for the
    // account with the memberships it kept through the disable. The admin's
    // alone, like the disable. No guard applies -- enabling widens nothing
    // an account did not already hold, and nobody locks themselves out by
    // enabling someone.
    private static async Task<IResult> EnableAsync(
        string operatorId,
        ClaimsPrincipal user,
        IOperatorRepository operators,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));
        if (!user.IsDeploymentAdmin())
            return Results.Forbid();

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

        if (!target.Disabled)
            return Results.Ok(new { operatorId = target.Id.ToString(), disabled = false });

        var updated = target.WithDisabled(false);
        await operators.SaveAsync(updated, cancellationToken);
        await RecordAccountChangeAsync(audit, user.TryGetOperatorId()!.Value, updated.Id,
            "operator.enabled", $"enabled '{updated.Handle}' (its memberships waited through the disable)", clock, cancellationToken);
        return Results.Ok(new { operatorId = updated.Id.ToString(), disabled = false });
    }

    // API-token management (architecture.md Sec 9 -- the identity model's API
    // tokens): a bearer credential minted per operator, honored by the
    // operator API alongside cookie sessions, and revocable like credentials.
    // The plaintext secret is shown exactly once, at mint; only its digest is
    // stored. Self-service on the caller's own account, the admin's alone on
    // anyone else's -- a token minted for another operator hands over their
    // identity. A token is independent of the password credential -- each
    // revokes through its own route -- so rotating one never silently
    // invalidates the other.
    private static async Task<IResult> MintTokenAsync(
        string operatorId,
        ClaimsPrincipal user,
        IOperatorRepository operators,
        IOperatorApiTokenStore tokens,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));
        if (!MayActOn(user, new OperatorId(operatorValue)))
            return Results.Forbid();

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

        var minted = await tokens.MintAsync(target.Id, clock.GetUtcNow(), cancellationToken);
        await RecordAccountChangeAsync(audit, user.TryGetOperatorId()!.Value, target.Id,
            "operator.token-minted", $"API token minted for '{target.Handle}' (the secret is shown once, never stored)", clock, cancellationToken);
        return Results.Ok(new MintedTokenResponse(
            minted.TokenId.ToString(),
            minted.Secret,
            minted.CreatedAt));
    }

    private static async Task<IResult> ListTokensAsync(
        string operatorId,
        ClaimsPrincipal user,
        IOperatorRepository operators,
        IOperatorApiTokenStore tokens,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));
        if (!MayActOn(user, new OperatorId(operatorValue)))
            return Results.Forbid();

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

        var rows = await tokens.ListAsync(target.Id, cancellationToken);
        return Results.Ok(rows.Select(r => new TokenResponse(r.TokenId.ToString(), r.CreatedAt)));
    }

    private static async Task<IResult> RevokeTokenAsync(
        string operatorId,
        string tokenId,
        ClaimsPrincipal user,
        IOperatorRepository operators,
        IOperatorApiTokenStore tokens,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));
        if (!MayActOn(user, new OperatorId(operatorValue)))
            return Results.Forbid();
        if (!Guid.TryParse(tokenId, out var tokenValue))
            return Results.BadRequest(new Problem("Token id is not a valid identifier."));

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

        // Idempotent: revoking an unknown token succeeds, like credential
        // revocation. The next request presenting it fails -- the digest is
        // read fresh on every attempt.
        await tokens.RevokeAsync(target.Id, new OperatorApiTokenId(tokenValue), cancellationToken);
        await RecordAccountChangeAsync(audit, user.TryGetOperatorId()!.Value, target.Id,
            "operator.token-revoked", $"API token {tokenValue} revoked for '{target.Handle}'", clock, cancellationToken);
        return Results.Ok(new { operatorId = target.Id.ToString(), tokenId });
    }

    // Revocation is the operator half of certificate revocation
    // (architecture.md Sec 9): deleting the stored verifier makes the next
    // login fail -- the hash is read fresh per attempt, so no restart is
    // involved. It ends the credential's live cookie sessions too: each
    // authenticated request revalidates the session stamp the login baked
    // into the cookie, and with the verifier gone the stamp no longer
    // matches, so the next request on that cookie is refused. Self-service
    // on the caller's own account, the admin's alone on anyone else's; the
    // action is idempotent.
    private static async Task<IResult> RevokeCredentialAsync(
        string operatorId,
        ClaimsPrincipal user,
        IOperatorRepository operators,
        IOperatorCredentialStore credentials,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operatorId, out var operatorValue))
            return Results.BadRequest(new Problem("Operator id is not a valid identifier."));
        if (!MayActOn(user, new OperatorId(operatorValue)))
            return Results.Forbid();

        var target = await operators.FindAsync(new OperatorId(operatorValue), cancellationToken);
        if (target is null)
            return Results.NotFound(new Problem($"Operator {operatorId} does not exist."));

        await credentials.RevokeAsync(target.Id, cancellationToken);
        await RecordAccountChangeAsync(audit, user.TryGetOperatorId()!.Value, target.Id,
            "operator.credential-revoked", $"credential revoked on '{target.Handle}' (next login fails; live sessions end)", clock, cancellationToken);
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
        // teamserver, so repeated failures put the handle -- and, on the
        // wider spray budget, the source address -- into a cooldown
        // instead of allowing unbounded online brute force (Sec 9).
        if (!throttle.IsAllowed(handle, remote))
        {
            logger.LogWarning("Login for handle {Handle} from {Remote} refused: cooldown active.", handle, remote);
            return Results.Json(new Problem("Too many failed attempts; try again later."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var result = await auth.TryLoginAsync(handle, body.Password, cancellationToken);
        if (!result.Success || result.Principal is null || result.Operator is null)
        {
            throttle.RecordFailure(handle, remote);
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
        => new(op.Id.Value, op.Handle, op.CreatedAt, DeploymentRoles.ToName(op.DeploymentRole));

    private static OperatorAccountResponse ToAccount(Operator op, bool hasCredential)
        => new(op.Id.Value, op.Handle, op.CreatedAt, hasCredential, op.Disabled,
            DeploymentRoles.ToName(op.DeploymentRole));
}

/// <summary>Login credentials submitted to <c>POST /operators/login</c>.</summary>
public sealed record LoginRequest(string Handle, string Password);

/// <summary>
/// A new account submitted to <c>POST /operators</c>: handle and initial
/// password are required. The account comes up as a deployment member --
/// reach arrives per engagement, as memberships its owners grant.
/// </summary>
public sealed record CreateOperatorRequest(string? Handle, string? Password);

/// <summary>
/// The replacement password submitted to <c>PUT /operators/{id}/credentials</c>.
/// The current password is required and must verify when the caller acts on
/// its own account (self-service proves possession); an admin acting on
/// another account omits it.
/// </summary>
public sealed record CredentialSetRequest(string? Password, string? CurrentPassword);

/// <summary>
/// The authenticated operator returned by login and <c>GET /operators/me</c>:
/// identity, the account's creation time, and deployment role -- the role is
/// the one privilege the account holds; everything else the operator may
/// reach resolves per engagement.
/// </summary>
public sealed record OperatorAuthSummary(Guid Id, string Handle, DateTimeOffset CreatedAt, string Role);

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
/// re-provisioned), whether the account is disabled (no authentication
/// path accepts it until enabled), and its deployment role ("admin" or
/// "member", the one privilege the account holds).
/// </summary>
public sealed record OperatorAccountResponse(
    Guid Id,
    string Handle,
    DateTimeOffset CreatedAt,
    bool HasCredential,
    bool Disabled,
    string Role);
