using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Rod.CoreState;
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

    // One hash of an unguessable throwaway password, minted on first use.
    // Unknown handles verify against it so a login attempt costs the KDF
    // either way: response timing cannot enumerate valid handles.
    private readonly Lazy<string> _dummyHash;

    // A stand-in account for the dummy verification: the password hasher
    // never reads the user, but the signature wants one.
    private static Operator Decoy => new(OperatorId.New(), "timing-decoy", DateTimeOffset.UnixEpoch);

    public OperatorAuthService(
        IOperatorRepository operators,
        IOperatorCredentialStore credentials,
        IPasswordHasher<Operator> hasher)
    {
        _operators = operators;
        _credentials = credentials;
        _hasher = hasher;
        _dummyHash = new Lazy<string>(
            () => _hasher.HashPassword(Decoy, Guid.NewGuid().ToString("N")));
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
        // circuits to a failed login without touching the store. A disabled
        // account fails the same way -- an attacker cannot tell a disabled
        // handle from an unknown one -- and every failure path pays the KDF
        // against the dummy hash, so timing cannot tell them apart either.
        var op = string.IsNullOrWhiteSpace(handle)
            ? null
            : await _operators.FindByHandleAsync(handle, cancellationToken);

        if (op is null || op.Disabled)
        {
            _ = _hasher.VerifyHashedPassword(Decoy, _dummyHash.Value, password ?? string.Empty);
            return OperatorLoginResult.Failed;
        }

        var hash = await _credentials.FindHashAsync(op.Id, cancellationToken);
        if (hash is null)
        {
            _ = _hasher.VerifyHashedPassword(Decoy, _dummyHash.Value, password ?? string.Empty);
            return OperatorLoginResult.Failed;
        }

        var verification = _hasher.VerifyHashedPassword(op, hash, password ?? string.Empty);
        if (verification == PasswordVerificationResult.Failed)
            return OperatorLoginResult.Failed;

        // A hash stored under older KDF parameters upgrades in place on
        // success -- the session is trusted either way, and the stored
        // hash converges on the configured strength without a migration
        // step. The principal carries the stamp of the hash that actually
        // verified, so the upgrade invalidates nothing mid-flight.
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var upgraded = _hasher.HashPassword(op, password ?? string.Empty);
            await _credentials.SetHashAsync(op.Id, upgraded, cancellationToken);
            hash = upgraded;
        }

        return OperatorLoginResult.Succeeded(op, CreatePrincipal(op, hash));
    }

    /// <summary>
    /// Builds the principal persisted as the cookie session: the operator id
    /// under <see cref="OperatorClaims.OperatorIdClaimType"/> and its handle
    /// under <see cref="OperatorClaims.OperatorHandleClaimType"/> (so
    /// transport can resolve the operator identity off the principal without
    /// referencing this layer), the handle as the name, and the
    /// authentication scheme as the identity label. The principal carries
    /// exactly one privilege, the deployment role claim: the management
    /// surfaces the admin gate answers to. Everything else -- what the
    /// operator may reach inside an engagement -- resolves fresh from the
    /// engagement memberships on every engagement-scoped request
    /// (architecture.md Sec 3). With <paramref name="verifierHash"/> it also
    /// carries the session stamp that binds the session to that credential
    /// generation.
    /// </summary>
    public static ClaimsPrincipal CreatePrincipal(Operator op, string? verifierHash = null)
    {
        var identity = new ClaimsIdentity(
            OperatorAuthConstants.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        identity.AddClaim(new Claim(OperatorClaims.OperatorIdClaimType, op.Id.Value.ToString()));
        identity.AddClaim(new Claim(OperatorClaims.OperatorHandleClaimType, op.Handle));
        identity.AddClaim(new Claim(
            OperatorClaims.DeploymentRoleClaimType, DeploymentRoles.ToName(op.DeploymentRole)));
        identity.AddClaim(new Claim(ClaimTypes.Name, op.Handle));
        if (verifierHash is not null)
            identity.AddClaim(SessionStamp.Claim(verifierHash));
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
