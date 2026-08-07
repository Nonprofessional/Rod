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
/// remaining core verb, per recon verb, per lateral verb, per persist verb, per
/// collect verb, per exfil verb, per evasion verb, and per exploit verb, so the
/// registry lists the full core, recon, lateral, persist, collect, exfil,
/// evasion, and exploit sets. A real module registered later for the same verb
/// replaces the placeholder (the last registration wins -- see
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
    /// (core plus recon plus lateral plus persist plus collect plus exfil plus
    /// evasion plus exploit). The walking-skeleton convenience for tests and for a
    /// process that does not run the full ASP.NET Core host: it owns one registry,
    /// loads the verbs into it, and hands it back ready to dispatch
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
    /// core verb, per recon verb, per lateral verb, per persist verb, per collect
    /// verb, per exfil verb, per evasion verb, and per exploit verb so the registry
    /// lists all eight full sets. Idempotent: each verb is registered at most once
    /// by deduplicating against what <paramref name="registry"/> already holds.
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

    /// <summary>
    /// Backward-compatible alias for <see cref="LoadCapabilitiesAsync"/>. Loads
    /// the core, recon, lateral, persist, collect, exfil, evasion, and exploit
    /// sets; kept under the original name so callers and tests from the M2.5
    /// skeleton keep compiling.
    /// </summary>
    public static Task LoadCoreCapabilitiesAsync(
        ICapabilityRegistry registry,
        CancellationToken cancellationToken = default)
        => LoadCapabilitiesAsync(registry, cancellationToken);

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
