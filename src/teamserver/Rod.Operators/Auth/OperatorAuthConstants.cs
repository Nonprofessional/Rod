namespace Rod.Operators.Auth;

/// <summary>
/// The operator authentication scheme names. A named front scheme (rather
/// than <c>CookieAuthenticationDefaults.AuthenticationScheme</c>) keeps the
/// operator session distinct from any other scheme the host may later carry,
/// and makes the scheme explicit at every call site.
/// </summary>
public static class OperatorAuthConstants
{
    /// <summary>
    /// The front scheme every endpoint names: a policy scheme that
    /// authenticates through <see cref="CookieScheme"/> or
    /// <see cref="TokenScheme"/> depending on what the request presents -- a
    /// session cookie or a bearer token -- while challenges, sign-in, and
    /// sign-out stay on the cookie scheme.
    /// </summary>
    public const string AuthenticationScheme = "Rod.Operator";

    /// <summary>The cookie session scheme behind the front scheme.</summary>
    public const string CookieScheme = "Rod.Operator.Cookie";

    /// <summary>The API-token bearer scheme behind the front scheme.</summary>
    public const string TokenScheme = "Rod.Operator.Token";
}
