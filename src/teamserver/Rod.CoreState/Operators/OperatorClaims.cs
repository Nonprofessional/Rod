using System.Security.Claims;

namespace Rod.CoreState.Operators;

/// <summary>
/// The claim type and read helper that bridge the operator-auth layer and the
/// transport layer. The auth layer in <c>Rod.Operators</c> issues the claim when
/// it signs an operator into a cookie session; the transport layer reads it back
/// to attribute each request to the authenticated operator -- but transport
/// cannot reference <c>Rod.Operators</c> (architecture test
/// <c>LayerDependencyTests</c>), so the contract the two share lives here, in the
/// inner ring they both depend on.
/// </summary>
/// <remarks>
/// This is the only auth concept core state carries: the claim names the auth
/// layer stamps and the readers the transport layer resolves an authenticated
/// request back to an operator with. The password verifier, the cookie, and the
/// login flow stay in the outer auth layer; core state never hashes, stores, or
/// interprets a password.
/// </remarks>
public static class OperatorClaims
{
    /// <summary>
    /// The claim that carries the authenticated operator's id, as the string
    /// form of its underlying <see cref="Guid"/>.
    /// </summary>
    public const string OperatorIdClaimType = "rod:operator-id";

    /// <summary>The claim that carries the authenticated operator's handle.</summary>
    public const string OperatorHandleClaimType = "rod:operator-handle";

    /// <summary>
    /// The claim that carries the authenticated operator's deployment role,
    /// as its wire name ("admin" / "member", <see cref="DeploymentRoles"/>).
    /// Stamped at login and token resolution from the account row; the role
    /// is fixed at registration, so the claim cannot go stale.
    /// </summary>
    public const string DeploymentRoleClaimType = "rod:deployment-role";

    /// <summary>
    /// Whether the principal carries the admin deployment role -- the role
    /// the deployment's management surfaces (other operators' accounts, the
    /// redirector fleet, the runtime settings) gate on. False for members
    /// and anonymous principals.
    /// </summary>
    public static bool IsDeploymentAdmin(this ClaimsPrincipal principal)
        => principal.HasClaim(DeploymentRoleClaimType, DeploymentRoles.AdminName);

    /// <summary>
    /// Reads the authenticated operator's id from a principal, or null when the
    /// principal has no operator-id claim (the caller is anonymous or the claim
    /// is malformed).
    /// </summary>
    public static OperatorId? TryGetOperatorId(this ClaimsPrincipal principal)
    {
        var claim = principal.FindFirst(OperatorIdClaimType);
        if (claim is null || !Guid.TryParse(claim.Value, out var guid))
            return null;

        return new OperatorId(guid);
    }

    /// <summary>
    /// Reads the authenticated operator's identity (id and handle) off the
    /// principal, or null when the principal carries no operator-id claim.
    /// The handle falls back to the operator id string when its claim is
    /// absent, so a principal stamped before that claim existed still
    /// resolves to a usable identity. The live-event stream uses this to
    /// attribute its presence frame to the signed-in operator without a
    /// per-connect repository lookup.
    /// </summary>
    public static OperatorIdentity? TryGetOperatorIdentity(this ClaimsPrincipal principal)
    {
        var id = principal.TryGetOperatorId();
        if (id is null)
            return null;

        var handle = principal.FindFirst(OperatorHandleClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(handle))
            handle = id.Value.Value.ToString();

        return new OperatorIdentity(id.Value, handle);
    }
}

/// <summary>
/// The authenticated operator's identity, read off the session principal. A
/// small value type so call sites (the live-event stream's presence join) take
/// one resolved identity rather than separate claim lookups.
/// </summary>
public readonly record struct OperatorIdentity(OperatorId Id, string Handle);
