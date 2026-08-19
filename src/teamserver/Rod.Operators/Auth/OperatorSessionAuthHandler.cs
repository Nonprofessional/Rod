using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Rod.Operators.Auth;

/// <summary>
/// The front operator-authentication scheme (architecture.md Sec 9): routes
/// each authentication operation to the credential the request actually
/// presents. A request carrying <c>Authorization: Bearer</c> authenticates
/// through the API-token scheme; everything else -- and every challenge,
/// forbid, sign-in, and sign-out -- goes to the cookie scheme, so the login
/// flow and the bare 401/403 shapes are exactly what they were. Hand-rolled
/// because the framework's policy scheme forwards per scheme, not per
/// operation: sign-in must always land on the cookie regardless of what header
/// a request carries.
/// </summary>
internal sealed class OperatorSessionAuthHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>, IAuthenticationSignInHandler, IAuthenticationSignOutHandler
{
    public OperatorSessionAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Request.Headers.Authorization.ToString()
            .StartsWith(OperatorTokenAuthHandler.AuthorizationPrefix, StringComparison.OrdinalIgnoreCase)
            ? Context.AuthenticateAsync(OperatorAuthConstants.TokenScheme)
            : Context.AuthenticateAsync(OperatorAuthConstants.CookieScheme);

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        => Context.ChallengeAsync(OperatorAuthConstants.CookieScheme, properties);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => Context.ForbidAsync(OperatorAuthConstants.CookieScheme, properties);

    public Task SignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties)
        => Context.SignInAsync(OperatorAuthConstants.CookieScheme, user, properties);

    public Task SignOutAsync(AuthenticationProperties? properties)
        => Context.SignOutAsync(OperatorAuthConstants.CookieScheme, properties);
}
