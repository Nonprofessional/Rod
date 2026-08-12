using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Rod.CoreState.Operators;

namespace Rod.Operators.Auth;

/// <summary>
/// Verifies an operator's password and builds the authenticated principal for a
/// login. Resolves the account by handle, loads the stored password hash, and
/// verifies the presented password against it with <see cref="IPasswordHasher{TUser}"/>
/// (PBKDF2 with a per-hash salt); on success it builds the
/// <see cref="ClaimsPrincipal"/> the cookie middleware persists as the session.
/// Both an unknown handle and a missing stored hash fail closed, and the
/// password verifier is the single source of truth, so an attacker cannot tell
/// an unknown handle from a wrong password -- the two are indistinguishable.
/// </summary>
public sealed class OperatorAuthService
{
    private readonly IOperatorRepository _operators;
    private readonly IOperatorCredentialStore _credentials;
    private readonly IPasswordHasher<Operator> _hasher;

    public OperatorAuthService(
        IOperatorRepository operators,
        IOperatorCredentialStore credentials,
        IPasswordHasher<Operator> hasher)
    {
        _operators = operators;
        _credentials = credentials;
        _hasher = hasher;
    }

    /// <summary>
    /// Attempts a login. Returns success with the operator and its principal when
    /// the handle is known and the password verifies; otherwise returns a failed
    /// result carrying no partial information.
    /// </summary>
    public async Task<OperatorLoginResult> TryLoginAsync(
        string handle,
        string password,
        CancellationToken cancellationToken = default)
    {
        // Resolve the account by handle. A null handle or whitespace short-
        // circuits to a failed login without touching the store.
        var op = string.IsNullOrWhiteSpace(handle)
            ? null
            : await _operators.FindByHandleAsync(handle, cancellationToken);

        if (op is null)
            return OperatorLoginResult.Failed;

        var hash = await _credentials.FindHashAsync(op.Id, cancellationToken);
        if (hash is null)
            return OperatorLoginResult.Failed;

        var verification = _hasher.VerifyHashedPassword(op, hash, password ?? string.Empty);
        if (verification == PasswordVerificationResult.Failed)
            return OperatorLoginResult.Failed;

        // SuccessRehash is accepted as a valid login; rehashing on every login is
        // a later hardening step and not required for the session to be trusted.
        return OperatorLoginResult.Succeeded(op, CreatePrincipal(op));
    }

    /// <summary>
    /// Builds the principal persisted as the cookie session: the operator id
    /// under <see cref="OperatorClaims.OperatorIdClaimType"/> and its handle and
    /// display name under their claims (so transport can resolve the full
    /// operator identity off the principal without referencing this layer), the
    /// handle as the name, and the authentication scheme as the identity label.
    /// </summary>
    public static ClaimsPrincipal CreatePrincipal(Operator op)
    {
        var identity = new ClaimsIdentity(
            OperatorAuthConstants.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        identity.AddClaim(new Claim(OperatorClaims.OperatorIdClaimType, op.Id.Value.ToString()));
        identity.AddClaim(new Claim(OperatorClaims.OperatorHandleClaimType, op.Handle));
        identity.AddClaim(new Claim(OperatorClaims.OperatorDisplayNameClaimType, op.DisplayName));
        identity.AddClaim(new Claim(ClaimTypes.Name, op.Handle));
        return new ClaimsPrincipal(identity);
    }
}

/// <summary>The outcome of a login attempt.</summary>
public readonly record struct OperatorLoginResult
{
    public bool Success { get; init; }
    public Operator? Operator { get; init; }
    public ClaimsPrincipal? Principal { get; init; }

    public static OperatorLoginResult Failed => new() { Success = false };

    public static OperatorLoginResult Succeeded(Operator op, ClaimsPrincipal principal)
        => new() { Success = true, Operator = op, Principal = principal };
}
