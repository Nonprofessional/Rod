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

    /// <summary>The claim that carries the authenticated operator's display name.</summary>
    public const string OperatorDisplayNameClaimType = "rod:operator-display-name";

    /// <summary>
    /// The claim that carries the authenticated operator's scope set, as the
    /// canonical value <see cref="OperatorScopes.ToClaimValue"/> renders
    /// (architecture.md Sec 4.5). Stamped wherever the principal is built --
    /// cookie login and API-token authentication -- and revalidated against
    /// the store per request, so a role change reaches the next request.
    /// </summary>
    public const string OperatorScopesClaimType = "rod:operator-scopes";

    /// <summary>
    /// Reads the authenticated operator's scope set off the principal. A
    /// principal stamped before scopes existed carries no claim; it resolves
    /// to <see cref="OperatorScope.None"/> and the per-request store
    /// comparison replaces it with the operator's current set.
    /// </summary>
    public static OperatorScope TryGetScopes(this ClaimsPrincipal principal)
        => OperatorScopes.FromClaimValue(principal.FindFirst(OperatorScopesClaimType)?.Value);

    /// <summary>
    /// Whether the principal's operator holds a scope -- the read the
    /// authorization policies and the endpoint gates share.
    /// </summary>
    public static bool HasScope(this ClaimsPrincipal principal, OperatorScope scope)
        => principal.TryGetScopes().HasFlag(scope);

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
    /// Reads the full authenticated-operator identity (id, handle, display name)
    /// off the principal, or null when the principal carries no operator-id
    /// claim. The handle and display name fall back to the operator id / handle
    /// string when their claims are absent, so a principal stamped before those
    /// claims existed still resolves to a usable identity. The live-event stream
    /// uses this to attribute its presence frame to the signed-in operator
    /// without a per-connect repository lookup.
    /// </summary>
    public static OperatorIdentity? TryGetOperatorIdentity(this ClaimsPrincipal principal)
    {
        var id = principal.TryGetOperatorId();
        if (id is null)
            return null;

        var handle = principal.FindFirst(OperatorHandleClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(handle))
            handle = id.Value.Value.ToString();

        var displayName = principal.FindFirst(OperatorDisplayNameClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = handle;

        return new OperatorIdentity(id.Value, handle, displayName);
    }
}

/// <summary>
/// The authenticated operator's identity, read off the session principal. A
/// small value type so call sites (the live-event stream's presence join) take
/// one resolved identity rather than three separate claim lookups.
/// </summary>
public readonly record struct OperatorIdentity(OperatorId Id, string Handle, string DisplayName);
