using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Endpoints;

/// <summary>
/// The operator-facing capability-catalog endpoint (): lists every
/// registered capability verb so the operator UI can surface the full capability
/// set as tasking from the registry rather than a hardcoded verb table. The
/// catalog carries each verb's category and OPSEC attributes (architecture.md
/// Sec 7/10.1) so the UI can group verbs by operational purpose and flag risky
/// actions. Sensitive categories (evasion, exploit) are listed too -- this layer
/// holds only the contract and dispatch path, never concrete tradecraft
/// (architecture.md Sec 13, AGENTS.md Sec 7).
/// </summary>
/// <remarks>
/// Mapped by the composition root alongside <c>MapOperatorEndpoints</c>, the same
/// way the operator layer maps its SSE stream: transport cannot reference
/// <c>Rod.Tradecraft</c> (architecture test <c>LayerDependencyTests</c>), so the
/// catalog endpoint is exposed from the layer that owns the registry. The catalog
/// is global (not engagement-scoped): capability verbs are the language-neutral
/// contract implants build against, independent of any one engagement.
/// </remarks>
public static class CapabilityEndpoints
{
    public static IEndpointRouteBuilder MapCapabilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Operator-facing: the capability catalog requires an authenticated
        // operator session.
        var group = endpoints.MapGroup("/capabilities").RequireAuthorization();
        group.MapGet("/", ListAsync).WithName("ListCapabilities");
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ICapabilityRegistry registry,
        CancellationToken cancellationToken)
    {
        // Registration order is stable (the registry preserves it), so the catalog
        // reads in a predictable category order for the UI's grouping.
        var descriptors = await registry.ListAsync(cancellationToken);
        var body = descriptors.Select(CapabilityDescriptorResponse.Of).ToArray();
        return Results.Ok(body);
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    /// <summary>
    /// One capability a registered module provides. The verb is the tasking
    /// identifier (<c>namespace.action</c>); <see cref="Category"/> groups it for
    /// the UI; <see cref="Attributes"/> is the per-verb OPSEC surface
    /// (architecture.md Sec 7) the UI surfaces as risk badges.
    /// </summary>
    public sealed record CapabilityDescriptorResponse(
        string Verb,
        string Category,
        string Version,
        IReadOnlyDictionary<string, string> Attributes)
    {
        public static CapabilityDescriptorResponse Of(Capabilities.CapabilityDescriptor d)
            => new(
                d.Verb,
                d.Category.ToString(),
                d.Version,
                d.Attributes);
    }

}
