using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rod.CoreState.Live;
using Rod.Operators.Endpoints;
using Rod.Operators.Live;
using Rod.Operators.Presence;

namespace Rod.Operators;

/// <summary>
/// Composition-root hooks for the operator layer. The transport
/// layer terminates the operator API but is constrained by the architecture
/// tests to core state / protocol / audit only -- it cannot reference
/// <c>Rod.Operators</c>. So the operator layer exposes its own service and
/// endpoint registration here, and the composition root
/// (<c>Rod.TeamServer.Program</c> and the transport test host) calls these
/// alongside <c>AddRodTransport</c> / <c>MapRodEndpoints</c>. The layer rule
/// stays inward-only: dependency direction is operators -> core state / audit,
/// never the reverse.
/// </summary>
public static class RodOperatorsHost
{
    /// <summary>
    /// Registers the operator layer's services: the live-event bus (an
    /// in-memory, channel-backed fan-out, one stream per engagement) and the
    /// operator presence roster. Call after <c>AddRodTransport</c>. The bus
    /// registration replaces the no-op default <see cref="AddRodTransport"/>
    /// installed; presence is operator-layer-only.
    /// </summary>
    public static IServiceCollection AddRodOperators(this IServiceCollection services)
    {
        // Replace the no-op bus the transport host registered by default with the
        // real, channel-backed fan-out. Replace (not Add) so there is exactly one
        // ILiveEventBus and every consumer (TaskService, BeaconEndpoint, the SSE
        // endpoint) shares it.
        services.Replace(ServiceDescriptor.Singleton<ILiveEventBus, InMemoryLiveEventBus>());
        services.TryAddSingleton<OperatorPresenceService>();
        return services;
    }

    /// <summary>
    /// Maps the operator layer's endpoints: the SSE event stream that keeps an
    /// operator session live per engagement and pushes every engagement event.
    /// Call alongside <c>MapRodEndpoints</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapOperatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOperatorEventEndpoints();
        return endpoints;
    }
}
