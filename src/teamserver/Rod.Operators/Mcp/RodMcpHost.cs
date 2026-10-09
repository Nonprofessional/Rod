using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Rod.CoreState.Operators;

namespace Rod.Operators.Mcp;

/// <summary>
/// Composition-root hooks for the operator layer's MCP server (architecture.md
/// Sec 4, the agent surface over the operator API). Like <c>RodOperatorsHost</c>,
/// this lives in the operator layer because transport cannot reference it; the
/// composition root (and the test hosts) reach registration through
/// <c>AddRodOperators</c>, which folds <see cref="AddRodMcp"/> in.
///
/// The endpoint rides the operator front at <c>/mcp</c> behind the existing
/// operator authentication -- an MCP client presents an operator API token as
/// its bearer credential and receives the same principal the console's session
/// carries, so every scope rule applies unchanged. The session mode is
/// stateless: the toolset is read-only request/response work with no
/// server-to-client sampling or elicitation, so no session state needs to
/// outlive a call.
///
/// <see cref="MapRodMcp"/> is deliberately a separate composition-root act,
/// not part of <c>MapOperatorEndpoints</c>: the SDK's endpoint mapping pins
/// the host's configuration root (a file watcher that outlives host
/// disposal). The one long-lived teamserver host never notices; a test
/// process that churns hundreds of hosts exhausts the machine's inotify
/// budget if every host maps it. So it is mapped exactly where it is served.
/// </summary>
public static class RodMcpHost
{
    /// <summary>Registers the MCP server with its read-only toolset.</summary>
    public static IServiceCollection AddRodMcp(this IServiceCollection services)
    {
        services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation { Name = "Rod teamserver", Version = "1.0" };
        })
        .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
        .WithTools<RodReadTools>();
        return services;
    }

    /// <summary>
    /// Maps the MCP endpoint on the operator front at <c>/mcp</c>, gated on the
    /// read scope like every other read of the engagement's state.
    /// </summary>
    public static IEndpointRouteBuilder MapRodMcp(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMcp("/mcp").RequireAuthorization(OperatorScopes.ReadPolicy);
        return endpoints;
    }
}
