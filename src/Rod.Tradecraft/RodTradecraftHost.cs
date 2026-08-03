using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Modules;
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
/// The core verbs load through this layer: <see cref="LoadCoreCapabilitiesAsync"/>
/// registers the dispatchable <c>shell.exec</c> stub plus a placeholder per
/// remaining core verb, so the registry lists the full core set. A real module
/// registered later for the same verb replaces the placeholder (the last
/// registration wins -- see <see cref="ICapabilityRegistry"/>).
///
/// This milestone does not wire the dispatcher onto the live task path; that
/// arrives with the offensive-capability milestones. These hooks are stable for
/// it: a future ASP.NET Core composition root will resolve the
/// <see cref="InMemoryCapabilityRegistry"/> singleton, call
/// <see cref="LoadCoreCapabilitiesAsync"/> once at startup, and let out-of-tree
/// modules register against <see cref="ICapabilityRegistry"/> afterwards.
/// </remarks>
public static class RodTradecraftHost
{
    /// <summary>
    /// A fresh in-memory registry preloaded with the core capability verbs. The
    /// walking-skeleton convenience for tests and for a process that does not
    /// run the full ASP.NET Core host: it owns one registry, loads the core
    /// verbs into it, and hands it back ready to dispatch <c>shell.exec</c>.
    /// </summary>
    public static async Task<InMemoryCapabilityRegistry> BuildDefaultRegistryAsync(
        CancellationToken cancellationToken = default)
    {
        var registry = new InMemoryCapabilityRegistry();
        await LoadCoreCapabilitiesAsync(registry, cancellationToken);
        return registry;
    }

    /// <summary>
    /// Registers the built-in core capability modules into
    /// <paramref name="registry"/>: the dispatchable <c>shell.exec</c> stub,
    /// then a placeholder per remaining core verb so the registry lists all of
    /// them. Idempotent: each verb is registered at most once by deduplicating
    /// against what <paramref name="registry"/> already holds.
    /// </summary>
    public static async Task LoadCoreCapabilitiesAsync(
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
            if (already.Contains(descriptor.Verb))
                continue; // caller-supplied override; leave it in place

            await registry.RegisterAsync(new PlaceholderCapabilityModule(descriptor), cancellationToken);
        }
    }
}
