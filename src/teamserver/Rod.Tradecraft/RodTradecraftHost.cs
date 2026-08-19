using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rod.CoreState.Implants;
using Rod.Tradecraft.Collect;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Evasion;
using Rod.Tradecraft.Exfil;
using Rod.Tradecraft.Exploit;
using Rod.Tradecraft.Lateral;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Persist;
using Rod.Tradecraft.Recon;
using Rod.Tradecraft.Registry;
using Rod.Tradecraft.Tunnel;

namespace Rod.Tradecraft;

/// <summary>
/// Composition-root hooks for the pluggable tradecraft layer (architecture.md
/// Sec 4.1 layer 6). The layer holds the capability-module
/// contract (a registration, not an execution surface -- the server only gates
/// and forwards, architecture.md Sec 10.2/10.3) and the registry; concrete
/// tradecraft is supplied as separate, opt-in, out-of-tree modules
/// (architecture.md Sec 13, AGENTS.md Sec 7).
/// </summary>
/// <remarks>
/// Capabilities load through this layer: <see cref="LoadCapabilitiesAsync"/>
/// registers a placeholder per core verb, per recon verb, per lateral verb, per
/// persist verb, per collect verb, per exfil verb, per tunnel verb, per evasion
/// verb, and per exploit verb, so the registry lists the full core, recon,
/// lateral, persist, collect, exfil, tunnel, evasion, and exploit sets. A real
/// module registered later for the same verb replaces the placeholder (the
/// last registration wins -- see <see cref="ICapabilityRegistry"/>).
///
/// The layer wires onto the live task path through <see cref="AddRodTradecraft"/>:
/// it registers the in-memory capability registry (loaded with every built-in
/// verb) as a singleton and swaps core state's strict class-table resolver for
/// <see cref="CapabilityRegistryTaskResolver"/>. From then on task issuance
/// resolves a verb through the registry in addition to the per-class reduced
/// set, so the contract-and-dispatch-only categories -- evasion and exploit
/// (architecture.md Sec 10.2) -- are no longer refused before dispatch. Call
/// after <c>AddRodTransport</c>; the composition root
/// (<c>Rod.TeamServer.Program</c>) and the transport test host do this alongside
/// <c>AddRodOperators</c>.
/// </remarks>
public static class RodTradecraftHost
{
    /// <summary>
    /// Wires the tradecraft layer onto the live task path (architecture.md Sec
    /// 10.3): registers an in-memory <see cref="ICapabilityRegistry"/> preloaded
    /// with every built-in capability verb as a singleton, and swaps core state's
    /// strict class-table <see cref="ITaskCapabilityResolver"/> for
    /// <see cref="CapabilityRegistryTaskResolver"/>. From then on task issuance
    /// admits a verb the class set does not when a capability module is registered
    /// for it -- the path that opens dispatch for the contract-and-dispatch-only
    /// categories (architecture.md Sec 10.2). Call after <c>AddRodTransport</c>;
    /// an out-of-tree module registered afterwards against
    /// <see cref="ICapabilityRegistry"/> replaces the placeholder for its verb and
    /// stays the authority.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>RodOperatorsHost.AddRodOperators</c>: the layer exposes its own
    /// DI hook because transport -- which owns the host the live task path runs
    /// in -- cannot reference this assembly (architecture test
    /// <c>LayerDependencyTests</c>). The composition root calls this alongside
    /// <c>AddRodOperators</c>; the transport test host calls it through its
    /// <c>configureServices</c> hook.
    /// </remarks>
    public static IServiceCollection AddRodTradecraft(this IServiceCollection services)
        => services.AddRodTradecraft(configuration: null);

    /// <summary>
    /// Wires the tradecraft layer onto the live task path and loads any
    /// out-of-tree capability modules listed under <c>Tradecraft:Modules</c> in
    /// <paramref name="configuration"/> (architecture.md Sec 10.2). Each entry is
    /// a <c>Namespace.Type, AssemblyName</c> string; the module is instantiated at
    /// startup and registered after the built-in load, so it replaces the
    /// placeholder for its verb -- adding one never edits the composition root.
    /// A null configuration (the test host, or any host that does not opt in)
    /// keeps the built-in placeholders in place.
    /// </summary>
    public static IServiceCollection AddRodTradecraft(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration? configuration)
    {
        // One registry for the process; load the built-in verbs before the
        // container resolves anything. Out-of-tree modules load afterwards and
        // replace the placeholder for their verb (last registration wins).
        var registry = new InMemoryCapabilityRegistry();
        LoadCapabilitiesAsync(registry, CancellationToken.None).GetAwaiter().GetResult();
        if (configuration is not null)
        {
            // Loads the config-listed modules; a bad entry fails startup loudly
            // rather than leaving the verb on its placeholder.
            CapabilityModuleLoader.LoadAsync(registry: registry, configuration: configuration, cancellationToken: CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        services.AddSingleton<ICapabilityRegistry>(registry);

        // Replace core state's strict class-table default with the registry-backed
        // resolver, the same way AddRodOperators replaces the no-op live bus. A
        // factory resolver resolves the registry from the container at build time.
        services.Replace(ServiceDescriptor.Singleton<ITaskCapabilityResolver>(sp =>
            new CapabilityRegistryTaskResolver(sp.GetRequiredService<ICapabilityRegistry>())));

        return services;
    }

    /// <summary>
    /// Maps the tradecraft layer's endpoints: the capability catalog
    /// (<c>GET /capabilities</c>) that lets the operator UI surface every
    /// capability category as tasking from the registry rather than a hardcoded
    /// verb table. Call alongside <c>MapRodEndpoints</c>; the
    /// composition root calls it after <c>MapOperatorEndpoints</c>.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>RodOperatorsHost.MapOperatorEndpoints</c>: the layer exposes
    /// its own endpoint mapping because transport -- which owns the host the
    /// operator API runs in -- cannot reference this assembly (architecture test
    /// <c>LayerDependencyTests</c>).
    /// </remarks>
    public static IEndpointRouteBuilder MapCapabilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Endpoints.CapabilityEndpoints.MapCapabilityEndpoints(endpoints);
        return endpoints;
    }

    /// <summary>
    /// A fresh in-memory registry preloaded with the built-in capability verbs
    /// (core plus recon plus lateral plus persist plus collect plus exfil plus
    /// tunnel plus evasion plus exploit). Convenience for tests and for a
    /// process that does not run the full ASP.NET Core host: it owns one registry
    /// and loads the verbs into it.
    /// </summary>
    public static async Task<InMemoryCapabilityRegistry> BuildDefaultRegistryAsync(
        CancellationToken cancellationToken = default)
    {
        var registry = new InMemoryCapabilityRegistry();
        await LoadCapabilitiesAsync(registry, cancellationToken);
        return registry;
    }

    /// <summary>
    /// Registers every built-in capability module into <paramref name="registry"/>:
    /// a placeholder per core verb, per recon verb, per lateral verb, per persist
    /// verb, per collect verb, per exfil verb, per tunnel verb, per evasion verb,
    /// and per exploit verb so the registry lists all nine full sets. Idempotent:
    /// each verb is registered at most once by deduplicating against what
    /// <paramref name="registry"/> already holds.
    /// </summary>
    public static async Task LoadCapabilitiesAsync(
        ICapabilityRegistry registry,
        CancellationToken cancellationToken = default)
    {
        var existing = await registry.ListAsync(cancellationToken);
        var already = new HashSet<string>(
            existing.Select(d => d.Verb),
            StringComparer.OrdinalIgnoreCase);

        // Every core verb registers as a placeholder: the server gates and
        // forwards only (architecture.md Sec 10.2/10.3), so no core verb has an
        // in-process implementation -- shell.exec included. Skip any verb a
        // caller already registered (e.g. an out-of-tree override loaded before
        // this call) -- the caller's module is the authority for that verb.
        foreach (var descriptor in CoreCapabilities.All)
        {
            await RegisterPlaceholderAsync(registry, descriptor, already, cancellationToken);
        }

        // Recon verbs load the same way: a placeholder per verb so the registry
        // lists the full recon set, leaving any caller-supplied override in
        // place. Concrete recon behavior is out-of-tree (architecture.md Sec 13).
        foreach (var descriptor in ReconCapabilities.All)
        {
            await RegisterPlaceholderAsync(registry, descriptor, already, cancellationToken);
        }

        // Lateral verbs load the same way: a placeholder per verb so the registry
        // lists the full lateral set, leaving any caller-supplied override in
        // place. Concrete lateral-movement behavior is out-of-tree
        // (architecture.md Sec 13).
        foreach (var descriptor in LateralCapabilities.All)
        {
            await RegisterPlaceholderAsync(registry, descriptor, already, cancellationToken);
        }

        // Persist verbs load the same way: a placeholder per verb so the registry
        // lists the full persist set, leaving any caller-supplied override in
        // place. Concrete persistence behavior is out-of-tree (architecture.md
        // Sec 13); the reference implants ship none.
        foreach (var descriptor in PersistCapabilities.All)
        {
            await RegisterPlaceholderAsync(registry, descriptor, already, cancellationToken);
        }

        // Collect verbs load the same way: a placeholder per verb so the registry
        // lists the full collect set, leaving any caller-supplied override in
        // place. Concrete collection behavior is out-of-tree (architecture.md
        // Sec 13); the reference implants ship none.
        foreach (var descriptor in CollectCapabilities.All)
        {
            await RegisterPlaceholderAsync(registry, descriptor, already, cancellationToken);
        }

        // Exfil verbs load the same way: a placeholder per verb so the registry
        // lists the full exfil set, leaving any caller-supplied override in
        // place. Concrete exfiltration behavior is out-of-tree (architecture.md
        // Sec 13); the reference implants ship none.
        foreach (var descriptor in ExfilCapabilities.All)
        {
            await RegisterPlaceholderAsync(registry, descriptor, already, cancellationToken);
        }

        // Tunnel verbs load the same way: a placeholder per verb so the registry
        // lists the full tunnel set, leaving any caller-supplied override in
        // place. Concrete tunneling behavior runs on the implant as a live
        // channel (architecture.md Sec 10.3); the server gates and forwards
        // only.
        foreach (var descriptor in TunnelCapabilities.All)
        {
            await RegisterPlaceholderAsync(registry, descriptor, already, cancellationToken);
        }

        // Evasion verbs load the same way: a placeholder per verb so the registry
        // lists the full evasion set, leaving any caller-supplied override in
        // place. Evasion is a sensitive category (architecture.md Sec 10.2,
        // Sec 13, RESPONSIBLE-USE.md): concrete behavior is out-of-tree, supplied
        // as opt-in modules, and the reference implants ship none. Unlike the
        // recon, lateral, persist, collect, and exfil verbs these are not gated
        // to a class -- that decision belongs to the live task path, not to this
        // contract milestone.
        foreach (var descriptor in EvasionCapabilities.All)
        {
            await RegisterPlaceholderAsync(registry, descriptor, already, cancellationToken);
        }

        // Exploit verbs load the same way: a placeholder per verb so the registry
        // lists the full exploit set, leaving any caller-supplied override in
        // place. Exploit is a sensitive category (architecture.md Sec 10.2,
        // Sec 13, RESPONSIBLE-USE.md): concrete behavior is out-of-tree, supplied
        // as opt-in modules, and the reference implants ship none. Like the
        // evasion verbs these are not gated to a class -- that decision belongs to
        // the live task path, not to this contract milestone.
        foreach (var descriptor in ExploitCapabilities.All)
        {
            await RegisterPlaceholderAsync(registry, descriptor, already, cancellationToken);
        }
    }

    // Registers a placeholder for descriptor's verb unless the registry already
    // has a module for it (an out-of-tree override). Centralized so the core,
    // recon, lateral, persist, collect, exfil, evasion, and exploit loops share
    // one dedup rule and one placeholder path.
    private static async Task RegisterPlaceholderAsync(
        ICapabilityRegistry registry,
        CapabilityDescriptor descriptor,
        HashSet<string> already,
        CancellationToken cancellationToken)
    {
        if (already.Contains(descriptor.Verb))
            return; // caller-supplied override; leave it in place

        await registry.RegisterAsync(new PlaceholderCapabilityModule(descriptor), cancellationToken);
    }
}
