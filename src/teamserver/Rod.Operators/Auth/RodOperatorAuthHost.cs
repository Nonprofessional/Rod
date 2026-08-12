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
    /// Registers operator authentication: the cookie session scheme, the password
    /// hasher, the login service, the bootstrap account seed, and the bound
    /// options. Endpoints opt into the session with
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
            .AddCookie(OperatorAuthConstants.AuthenticationScheme, options =>
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
                // can route to the login view itself.
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
                };
            });

        services.AddAuthorization();

        services.AddSingleton<IPasswordHasher<Operator>, PasswordHasher<Operator>>();
        services.AddSingleton<OperatorAuthService>();
        services.AddHostedService<OperatorAuthBootstrap>();

        return services;
    }

    /// <summary>
    /// Maps the operator session endpoints under <c>/operators</c>:
    /// <c>POST /operators/login</c> (anonymous), <c>POST /operators/logout</c>,
    /// and <c>GET /operators/me</c>. Call alongside <c>MapOperatorEndpoints</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapOperatorAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/operators");
        OperatorAuthEndpoints.Map(group);
        return endpoints;
    }
}
