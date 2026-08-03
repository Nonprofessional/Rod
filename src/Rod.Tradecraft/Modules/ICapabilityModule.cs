using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Modules;

/// <summary>
/// One pluggable post-exploitation capability module (architecture.md Sec 4.1
/// layer 6, Sec 10/13). A module declares the single capability it provides via
/// <see cref="Descriptor"/> and executes invocations of that verb through
/// <see cref="ExecuteAsync"/>. Modules are stateless: the dispatcher holds them
/// by reference and may call <see cref="ExecuteAsync"/> concurrently.
/// </summary>
/// <remarks>
/// This repository ships only the contract, the registration, and the dispatch
/// path (AGENTS.md Sec 7, architecture.md Sec 13). Concrete tradecraft --
/// recon, lateral movement, persistence, collection, exfiltration, and any
/// evasion/exploit behavior -- is supplied as separate, opt-in, out-of-tree
/// modules that implement this interface. The built-in core-verb stub
/// (<c>CoreCapabilityModule</c>) exists only to prove the core verbs load and
/// dispatch through this contract; it produces no real tradecraft.
/// </remarks>
public interface ICapabilityModule
{
    /// <summary>
    /// The capability this module provides. Registered with the
    /// <see cref="Registry.ICapabilityRegistry"/> so the dispatcher can route
    /// invocations of <see cref="CapabilityDescriptor.Verb"/> here.
    /// </summary>
    CapabilityDescriptor Descriptor { get; }

    /// <summary>
    /// Executes <paramref name="invocation"/> and returns its outcome. The
    /// invocation's verb always matches <see cref="Descriptor"/>'s verb -- the
    /// dispatcher only routes a module its own verb -- so an implementation may
    /// assume it. Throwing is permitted for truly exceptional failures, but a
    /// recoverable failure should come back as
    /// <see cref="CapabilityResult.Failed"/>; the dispatcher does not catch.
    /// </summary>
    Task<CapabilityResult> ExecuteAsync(
        CapabilityInvocation invocation,
        CancellationToken cancellationToken = default);
}
