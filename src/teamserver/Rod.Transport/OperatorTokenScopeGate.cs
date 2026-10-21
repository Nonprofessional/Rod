using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState.Operators;
using Rod.Transport.Endpoints;

namespace Rod.Transport;

/// <summary>
/// The token-scope floor (architecture.md Sec 15.3, ADR-006): an API token
/// authenticates as its operator, but the scope it was minted at caps what
/// the credential may do -- read reads, operate acts inside engagements,
/// admin additionally reaches the deployment's management surface. A signed-in
/// operator's cookie session carries no scope claim and passes untouched: the
/// deployment role and the engagement memberships keep governing exactly as
/// they did. The gate narrows; it never widens.
/// </summary>
/// <remarks>
/// The floor is method-and-surface, deliberately coarse: reads are the
/// <c>read</c> floor, writes inside the engagement surface are
/// <c>operate</c>, and the deployment-management prefixes -- operator
/// accounts, the redirector fleet, the runtime settings, the system surface
/// -- take <c>admin</c> for any mutation. Finer authority (which engagement,
/// which verb) stays where it already lives: the engagement access filter and
/// the sensitive-verb policy run behind this gate and make their own calls
/// the same way they do for an operator's session. The MCP surface is exempt
/// by shape, not by omission: its toolset speaks JSON-RPC inside one POST
/// route, so this method-and-path gate can never see which tool a call
/// drives. The write toolset holds the operate floor at the tool level (the
/// same floor this gate gives every engagement-scoped write), so the
/// exemption opens no gap.
/// </remarks>
public static class OperatorTokenScopeGate
{
    /// <summary>
    /// Rejects token-authenticated requests beneath their scope. Runs after
    /// authentication, before the endpoint executes; requests with no scope
    /// claim (sessions, anonymous routes the endpoint itself guards) pass
    /// through untouched.
    /// </summary>
    public static IApplicationBuilder UseOperatorTokenScopeGate(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var scope = context.User.TryGetTokenScope();
            if (scope is { } capped && !Permits(capped, context.Request.Method, context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(
                    new Problem($"The API token's '{OperatorTokenScopes.ToName(capped)}' scope does not cover this action."),
                    context.RequestAborted);
                return;
            }
            await next(context);
        });

    private static bool Permits(OperatorTokenScope scope, string method, PathString path)
    {
        // Reads are the read floor -- every mintable scope covers them; the
        // engagement access filter behind this gate still decides membership.
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
            return true;

        // The MCP surface stays exempt by shape: one POST route carrying
        // JSON-RPC, so no method-and-path rule can see the tool a call
        // drives. The write toolset holds the operate floor itself.
        if (path.StartsWithSegments("/mcp"))
            return true;

        // The deployment's management surface (the prefixes OperatorClaims
        // names for the admin role) takes the admin scope for any mutation.
        if (path.StartsWithSegments("/operators")
            || path.StartsWithSegments("/redirectors")
            || path.StartsWithSegments("/settings")
            || path.StartsWithSegments("/system"))
            return scope >= OperatorTokenScope.Admin;

        // Everything else on the operator API is engagement-scoped acting:
        // tasking, input, notes, comments, chat, builds -- the operate floor.
        return scope >= OperatorTokenScope.Operate;
    }
}
