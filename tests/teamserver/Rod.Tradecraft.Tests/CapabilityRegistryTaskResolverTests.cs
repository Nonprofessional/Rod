using Rod.CoreState.Implants;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Evasion;
using Rod.Tradecraft.Exploit;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Registry;
using Task = System.Threading.Tasks.Task;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// Roadmap  at the resolver level: the capability-registry-backed
/// <see cref="ITaskCapabilityResolver"/> admits a verb the per-class reduced set
/// does not when a module is registered for it (architecture.md Sec 10.2/10.3).
/// The class table stays the primary authority; the registry opens the
/// contract-and-dispatch-only path for evasion and exploit.
/// </summary>
public class CapabilityRegistryTaskResolverTests
{
    [Fact]
    public async Task IsDispatchable_TrueForAVerbInTheClassSet()
    {
        // A class-gated verb is always dispatchable: the class table is checked
        // first and short-circuits before the registry is consulted.
        var resolver = new CapabilityRegistryTaskResolver(new InMemoryCapabilityRegistry());

        Assert.True(resolver.IsDispatchable(ImplantClass.Stage2, "shell.exec"));
        Assert.True(resolver.IsDispatchable(ImplantClass.Stager, "file.pull"));
    }

    [Fact]
    public async Task IsDispatchable_FalseForAnUnknownVerb()
    {
        // No module registered, and the verb is not in any class set: the gate
        // refuses it, so a nonsense verb never reaches the queue.
        var resolver = new CapabilityRegistryTaskResolver(new InMemoryCapabilityRegistry());

        Assert.False(resolver.IsDispatchable(ImplantClass.Stage2, "does.not.exist"));
    }

    [Fact]
    public async Task IsDispatchable_AdmitsAnEvasionVerbOnceRegistered()
    {
        // The built-in load registers a placeholder for every framework verb,
        // including the evasion verbs (architecture.md Sec 10.2). A placeholder is
        // a registered module, so it satisfies the gate: the evasion verb is no
        // longer refused before dispatch, even on a reduced class.
        var registry = new InMemoryCapabilityRegistry();
        await registry.RegisterAsync(
            new PlaceholderCapabilityModule(
                CapabilityDescriptor.Of(EvasionCapabilities.Avoid, CapabilityCategory.Evasion, "1.0")));
        var resolver = new CapabilityRegistryTaskResolver(registry);

        Assert.True(resolver.IsDispatchable(ImplantClass.Stager, EvasionCapabilities.Avoid));
        Assert.True(resolver.IsDispatchable(ImplantClass.WebShell, EvasionCapabilities.Avoid));
    }

    [Fact]
    public async Task IsDispatchable_AdmitsAnExploitVerbRegisteredByAnOutOfTreeModule()
    {
        // An operator-supplied, out-of-tree module (here a fixed-result stand-in)
        // registered for an exploit verb is the authority for it, and the resolver
        // admits the verb -- the "registered capability module reached" half of
        // the acceptance criteria at the resolver level.
        var registry = new InMemoryCapabilityRegistry();
        await registry.RegisterAsync(new FixedModule(ExploitCapabilities.Invoke));
        var resolver = new CapabilityRegistryTaskResolver(registry);

        Assert.True(resolver.IsDispatchable(ImplantClass.Stage2, ExploitCapabilities.Invoke));
    }

    [Fact]
    public async Task IsDispatchable_ClassTableAdmitsEvenWhenNoModuleRegistered()
    {
        // The class table is primary: a verb it admits is dispatchable even with
        // an empty registry, so the registry only ever widens the gate, never
        // narrows it.
        var resolver = new CapabilityRegistryTaskResolver(new InMemoryCapabilityRegistry());

        Assert.True(resolver.IsDispatchable(ImplantClass.Stage2, "recon.portscan"));
    }

    // A module whose result is fixed, standing in for an operator-supplied
    // out-of-tree exploit module without writing real tradecraft.
    private sealed class FixedModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }

        public FixedModule(string verb)
            => Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Exploit, "1.0");

        public Task<CapabilityResult> ExecuteAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CapabilityResult.Succeeded("out-of-tree"));
    }
}
