using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Modules;

namespace Rod.Tradecraft.Core;

/// <summary>
/// The built-in stub for the <c>shell.exec</c> core verb (architecture.md
/// Sec 10.1, roadmap M2.5). It exists so the skeleton can prove a module
/// registers and is dispatched through the tradecraft contract: dispatching
/// <c>shell.exec</c> against it returns a fixed, recognizable result.
/// </summary>
/// <remarks>
/// This is not tradecraft. Real <c>shell.exec</c> behavior runs on the implant
/// and its output is captured over the beacon stream (architecture.md Sec 10.3);
/// wiring this layer onto that live path arrives with the offensive-capability
/// milestones. Here the stub is the contract's proof of life.
/// </remarks>
public sealed class CoreCapabilityModule : ICapabilityModule
{
    /// <summary>The descriptor this module registers under.</summary>
    public CapabilityDescriptor Descriptor { get; } =
        CapabilityDescriptor.Of(CoreCapabilities.ShellExec, CapabilityCategory.Core, "1.0");

    /// <summary>
    /// Returns a fixed successful result echoing the invocation's arguments, so
    /// a dispatch round-trip is observable in a test without an implant.
    /// </summary>
    public Task<CapabilityResult> ExecuteAsync(
        CapabilityInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = string.IsNullOrWhiteSpace(invocation.Arguments)
            ? "shell.exec stub: (no arguments)"
            : $"shell.exec stub: {invocation.Arguments}";
        return Task.FromResult(CapabilityResult.Succeeded(output));
    }
}
