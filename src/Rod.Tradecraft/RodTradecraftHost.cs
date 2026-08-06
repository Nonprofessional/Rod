using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Lateral;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Recon;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft;

/// <summary>
/// Composition-root hooks for the pluggable tradecraft layer (architecture.md
/// Sec 4.1 layer 6, roadmap M2.5). The layer holds the capability-module
/// contract, the registry, and the dispatcher; concrete tradecraft is supplied
/// as separate, opt-in, out-of-tree modules (architecture.md Sec 13, AGENTS.md
/// Sec 7).
/// </summary>
/// <remarks>
/// Capabilities load through this layer: <see cref="LoadCapabilitiesAsync"/>
/// registers the dispatchable <c>shell.exec</c> stub plus a placeholder per
/// remaining core verb, per recon verb, and per lateral verb, so the registry
/// lists the full core, recon, and lateral sets. A real module registered later
/// for the same verb replaces the placeholder (the last registration wins -- see
/// <see cref="ICapabilityRegistry"/>).
///
/// This milestone does not wire the dispatcher onto the live task path; that
/// arrives with the offensive-capability milestones. These hooks are stable for
/// it: a future ASP.NET Core composition root will resolve the
/// <see cref="InMemoryCapabilityRegistry"/> singleton, call
/// <see cref="LoadCapabilitiesAsync"/> once at startup, and let out-of-tree
/// modules register against <see cref="ICapabilityRegistry"/> afterwards.
/// </remarks>
public static class RodTradecraftHost
{
    /// <summary>
    /// A fresh in-memory registry preloaded with the built-in capability verbs
    /// (core plus recon plus lateral). The walking-skeleton convenience for tests
    /// and for a process that does not run the full ASP.NET Core host: it owns one
    /// registry, loads the verbs into it, and hands it back ready to dispatch
    /// <c>shell.exec</c>.
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
    /// the dispatchable <c>shell.exec</c> stub, then a placeholder per remaining
    /// core verb, per recon verb, and per lateral verb so the registry lists all
    /// three full sets. Idempotent: each verb is registered at most once by
    /// deduplicating against what <paramref name="registry"/> already holds.
    /// </summary>
    public static async Task LoadCapabilitiesAsync(
        ICapabilityRegistry registry,
        CancellationToken cancellationToken = default)
    {
        var existing = await registry.ListAsync(cancellationToken);
        var already = new HashSet<string>(
            existing.Select(d => d.Verb),
            StringComparer.OrdinalIgnoreCase);

        // The dispatchable stub first; then a placeholder for every other core
        // verb so the full core set is listed. Skip any verb a caller already
        // registered (e.g. an out-of-tree override loaded before this call) --
        // the caller's module is the authority for that verb.
        if (!already.Contains(CoreCapabilities.ShellExec))
        {
            await registry.RegisterAsync(new CoreCapabilityModule(), cancellationToken);
        }

        foreach (var descriptor in CoreCapabilities.All)
        {
            if (string.Equals(descriptor.Verb, CoreCapabilities.ShellExec, StringComparison.OrdinalIgnoreCase))
                continue; // registered by the stub above
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
    }

    /// <summary>
    /// Backward-compatible alias for <see cref="LoadCapabilitiesAsync"/>. Loads
    /// the core, recon, and lateral sets; kept under the original name so callers
    /// and tests from the M2.5 skeleton keep compiling.
    /// </summary>
    public static Task LoadCoreCapabilitiesAsync(
        ICapabilityRegistry registry,
        CancellationToken cancellationToken = default)
        => LoadCapabilitiesAsync(registry, cancellationToken);

    // Registers a placeholder for descriptor's verb unless the registry already
    // has a module for it (an out-of-tree override). Centralized so the core,
    // recon, and lateral loops share one dedup rule and one placeholder path.
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
