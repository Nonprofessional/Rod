using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rod.CoreState.Operators;
using Rod.Operators.Endpoints;

namespace Rod.Operators.Auth;

/// <summary>
/// Composition-root hooks for operator authentication, the production-hardening
/// follow-on to the operator layer (architecture.md Sec 4). Like
/// <c>RodOperatorsHost</c>, this lives in the operator layer because transport
/// cannot reference it (architecture test <c>LayerDependencyTests</c>); the
/// composition root calls <see cref="AddRodOperatorAuth"/> alongside
/// <c>AddRodOperators</c> and <see cref="MapOperatorAuthEndpoints"/> alongside
/// <c>MapOperatorEndpoints"/>.
/// </summary>
public static class RodOperatorAuthHost
{
    /// <summary>
    /// Registers operator authentication: the cookie session scheme, the
    /// API-token bearer scheme, the policy scheme that fronts both, the
    /// password hasher, the login service, the bootstrap account seed, and the
    /// bound options. Endpoints opt into the session with
    /// <c>RequireAuthorization()</c>; the cookie is same-origin so the React SPA
    /// and its Server-Sent Events stream ride the same session without a token.
    /// </summary>
    public static IServiceCollection AddRodOperatorAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<OperatorAuthOptions>(configuration.GetSection("Operators"));

        services
            .AddAuthentication(OperatorAuthConstants.AuthenticationScheme)
            // The front scheme: authenticate through the API-token scheme when
            // a bearer token is presented, the cookie scheme otherwise; every
            // other operation (challenge, sign-in, sign-out) stays on the
            // cookie scheme, so the login flow and the 401/403 shapes are
            // unchanged.
            .AddScheme<AuthenticationSchemeOptions, OperatorSessionAuthHandler>(
                OperatorAuthConstants.AuthenticationScheme, _ => { })
            .AddCookie(OperatorAuthConstants.CookieScheme, options =>
            {
                options.Cookie.Name = "Rod.Operator.Auth";
                options.Cookie.HttpOnly = true;
                // The dev listener is loopback HTTP; SameAsRequest keeps the
                // cookie working there while still marking it Secure over HTTPS
                // in a real deployment.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);

                // The API is consumed by the SPA over fetch; a 302 to a login
                // page is the wrong response for an unauthenticated XHR. Turn the
                // cookie middleware's redirects into bare 401/403 so the client
                // can route to the login view itself. Validation bounds every
                // session against the credential generation that issued it.
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = ctx =>
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = ctx =>
                    {
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    },
                    OnValidatePrincipal = ValidateSessionAsync,
                };
            })
            .AddScheme<AuthenticationSchemeOptions, OperatorTokenAuthHandler>(
                OperatorAuthConstants.TokenScheme, _ => { });

        services.AddAuthorization(options =>
        {
            // The scope policies (architecture.md Sec 4.5): endpoint groups in
            // transport and this layer require them by name through the
            // constants core state carries, the same way the identity claims
            // cross the boundary. Assertion-based because the scope set is one
            // claim value holding a set, not one claim per scope.
            options.AddPolicy(OperatorScopes.ReadPolicy,
                policy => policy.RequireAssertion(ctx => ctx.User.HasScope(OperatorScope.Read)));
            options.AddPolicy(OperatorScopes.TaskPolicy,
                policy => policy.RequireAssertion(ctx => ctx.User.HasScope(OperatorScope.Task)));
        });

        services.AddSingleton<IPasswordHasher<Operator>, PasswordHasher<Operator>>();
        services.AddSingleton<OperatorAuthService>();
        // The per-handle login throttle shares the process clock; a successful
        // login resets the failure counter for its handle.
        services.AddSingleton<LoginThrottle>(sp => new LoginThrottle(sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService<OperatorAuthBootstrap>();

        return services;
    }

    /// <summary>
    /// Maps the operator session and account endpoints under
    /// <c>/operators</c>: <c>POST /operators/login</c> (anonymous),
    /// <c>POST /operators/logout</c>, <c>GET /operators/me</c>, the roster
    /// read and account provisioning (<c>GET/POST /operators</c>), and the
    /// credential and API-token management routes. Call alongside
    /// <c>MapOperatorEndpoints</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapOperatorAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/operators");
        OperatorAuthEndpoints.Map(group);
        return endpoints;
    }

    // The per-request session validation (architecture.md Sec 9): a cookie is
    // self-contained, so its lifetime is bounded against the credential
    // generation that issued it. The stamp the login baked into the principal
    // must match the stamp of the verifier the store holds now -- a revoked
    // credential (no verifier) or a re-provisioned one (a new verifier) fails
    // the comparison, and the principal is rejected at the request that
    // presented the cookie. Reading the verifier per attempt is the same
    // fresh-read discipline login applies.
    //
    // The same read bounds the session's scope set against the store
    // (architecture.md Sec 4.5): when the operator's scopes moved and the
    // cookie still carries the old set, the principal is replaced with the
    // current one at this very request -- a demotion takes effect immediately
    // and a promotion needs no re-login, and the renewed cookie carries the
    // new set so the comparison settles.
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        var operatorId = context.Principal?.TryGetOperatorId();
        if (operatorId is null)
        {
            context.RejectPrincipal();
            return;
        }

        var services = context.HttpContext.RequestServices;
        var credentials = services.GetRequiredService<IOperatorCredentialStore>();
        var hash = await credentials.FindHashAsync(operatorId.Value, context.HttpContext.RequestAborted);

        var stamp = context.Principal!.FindFirst(SessionStamp.ClaimType)?.Value;
        if (hash is null || stamp is null || stamp != SessionStamp.Compute(hash))
        {
            context.RejectPrincipal();
            return;
        }

        var @operator = await services.GetRequiredService<IOperatorRepository>()
            .FindAsync(operatorId.Value, context.HttpContext.RequestAborted);
        if (@operator is null)
        {
            context.RejectPrincipal();
            return;
        }

        if (context.Principal!.TryGetScopes() != @operator.Scopes)
            context.ReplacePrincipal(OperatorAuthService.CreatePrincipal(@operator, hash));
    }
}
