using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Rod.CoreState.Operators;

namespace Rod.Operators.Auth;

/// <summary>
/// The API-token half of operator authentication (architecture.md Sec 9 --
/// the identity model's API tokens): authenticates an
/// <c>Authorization: Bearer</c> header against the operator token store and
/// yields the same principal shape the cookie session carries, so every
/// authorized endpoint is reachable with either credential. The presented
/// secret is resolved fresh on every request -- revoking a token takes effect
/// at the next request, the same immediate-effect shape credential revocation
/// keeps. A token principal carries no session stamp: the token's own store
/// row is its validity, and the token is revoked by its own route, not by a
/// password change.
/// </summary>
internal sealed class OperatorTokenAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string AuthorizationPrefix = "Bearer ";

    private readonly IOperatorApiTokenStore _tokens;
    private readonly IOperatorRepository _operators;

    public OperatorTokenAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        IOperatorApiTokenStore tokens,
        IOperatorRepository operators)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
        _operators = operators;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        StringValues header;
        if (!Request.Headers.TryGetValue("Authorization", out header))
            return AuthenticateResult.NoResult();

        var value = header.ToString();
        if (!value.StartsWith(AuthorizationPrefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var secret = value[AuthorizationPrefix.Length..].Trim();
        if (secret.Length == 0)
            return AuthenticateResult.Fail("The bearer token is empty.");

        var operatorId = await _tokens.FindOperatorAsync(secret, Context.RequestAborted);
        if (operatorId is null)
            return AuthenticateResult.Fail("The API token is unknown or revoked.");

        var op = await _operators.FindAsync(operatorId.Value, Context.RequestAborted);
        if (op is null)
            return AuthenticateResult.Fail("The API token resolves to no operator.");

        var principal = OperatorAuthService.CreatePrincipal(op);
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, OperatorAuthConstants.TokenScheme));
    }
}
