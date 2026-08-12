namespace Rod.Operators.Auth;

/// <summary>
/// The cookie authentication scheme name used for operator sessions. A named
/// scheme (rather than <c>CookieAuthenticationDefaults.AuthenticationScheme</c>)
/// keeps the operator session distinct from any other cookie scheme the host may
/// later carry, and makes the scheme explicit at every call site.
/// </summary>
public static class OperatorAuthConstants
{
    public const string AuthenticationScheme = "Rod.Operator";
}
