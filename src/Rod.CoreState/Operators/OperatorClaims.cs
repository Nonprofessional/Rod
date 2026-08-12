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
/// This is the only auth concept core state carries: a claim name and a reader
/// for it. The password verifier, the cookie, and the login flow stay in the
/// outer auth layer; core state never hashes, stores, or interprets a password.
/// </remarks>
public static class OperatorClaims
{
    /// <summary>
    /// The claim that carries the authenticated operator's id, as the string
    /// form of its underlying <see cref="Guid"/>.
    /// </summary>
    public const string OperatorIdClaimType = "rod:operator-id";

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
}
