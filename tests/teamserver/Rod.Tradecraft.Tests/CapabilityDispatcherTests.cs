using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// Dispatcher behavior for the tradecraft layer skeleton (,
/// architecture.md Sec 10.3). <see cref="CapabilityDispatcher"/> resolves a verb
/// to its registered module and hands the invocation off; a verb with no module
/// is a normal <see cref="CapabilityStatus.NotFound"/> result rather than a
/// thrown exception, so a future task-issuance gate can treat "unhandled verb"
/// as a value.
/// </summary>
public class CapabilityDispatcherTests
{
    [Fact]
    public async Task Dispatch_RegisteredVerb_RoutesToTheModule()
    {
        var registry = new InMemoryCapabilityRegistry();
        await registry.RegisterAsync(new CoreCapabilityModule());
        var dispatcher = new CapabilityDispatcher(registry);

        var result = await dispatcher.DispatchAsync(
            new CapabilityInvocation(CoreCapabilities.ShellExec, "whoami"));

        Assert.Equal(CapabilityStatus.Succeeded, result.Status);
        Assert.Contains("whoami", result.Output);
    }

    [Fact]
    public async Task Dispatch_UnknownVerb_ReturnsNotFound()
    {
        // The skeleton must not throw on an unregistered verb: the live task
        // path will consult dispatch later, and "no module handles this" is a
        // value it decides on, not an exception it catches.
        var registry = new InMemoryCapabilityRegistry();
        var dispatcher = new CapabilityDispatcher(registry);

        var result = await dispatcher.DispatchAsync(CapabilityInvocation.Of("does.not.exist"));

        Assert.Equal(CapabilityStatus.NotFound, result.Status);
        Assert.Contains("does.not.exist", result.Error ?? string.Empty);
    }

    [Fact]
    public async Task Dispatch_PassesArgumentsThrough()
    {
        // The invocation's arguments reach the module unchanged; the dispatcher
        // adds no policy of its own (no authorization, no audit).
        var registry = new InMemoryCapabilityRegistry();
        var captured = new CapturingModule("recon.portscan");
        await registry.RegisterAsync(captured);
        var dispatcher = new CapabilityDispatcher(registry);

        await dispatcher.DispatchAsync(new CapabilityInvocation("recon.portscan", "10.0.0.0/24"));

        Assert.Equal("recon.portscan", captured.LastInvocation?.Verb);
        Assert.Equal("10.0.0.0/24", captured.LastInvocation?.Arguments);
    }

    // A module that records the last invocation it was handed, so the dispatcher
    // test can assert what reached the module.
    private sealed class CapturingModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }
        public CapabilityInvocation? LastInvocation { get; private set; }

        public CapturingModule(string verb)
        {
            Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Recon, "1.0");
        }

        public Task<CapabilityResult> ExecuteAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            LastInvocation = invocation;
            return Task.FromResult(CapabilityResult.Succeeded("ok"));
        }
    }
}
