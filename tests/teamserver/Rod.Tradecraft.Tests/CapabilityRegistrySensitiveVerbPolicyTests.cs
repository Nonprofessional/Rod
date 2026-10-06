using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Collect;
using Rod.Tradecraft.Core;
using Rod.Tradecraft.Evasion;
using Rod.Tradecraft.Exploit;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Persist;
using Rod.Tradecraft.Registry;

namespace Rod.Tradecraft.Tests;

/// <summary>
/// The registry-backed sensitivity judgment (architecture.md Sec 10.4, the
/// guards): a verb's automation posture rides its descriptor's OPSEC
/// metadata, so every registered verb -- the framework's or an out-of-tree
/// module's -- acquires its posture at registration, with no parallel list
/// to keep in step. The static floor stays loaded underneath and may only be
/// widened, never narrowed.
/// </summary>
public class CapabilityRegistrySensitiveVerbPolicyTests
{
    private static CapabilityRegistrySensitiveVerbPolicy NewPolicy(
        params ICapabilityModule[] modules)
    {
        var registry = new InMemoryCapabilityRegistry();
        foreach (var module in modules)
        {
            registry.RegisterAsync(module).GetAwaiter().GetResult();
        }
        return new CapabilityRegistrySensitiveVerbPolicy(registry);
    }

    [Theory]
    [InlineData(CollectCapabilities.Keylog)]        // reads-input
    [InlineData(CollectCapabilities.Minidump)]      // reads-memory
    [InlineData(EvasionCapabilities.InjectShellcode)] // executes-code
    [InlineData(EvasionCapabilities.Avoid)]         // the Evasion category
    [InlineData(EvasionCapabilities.Unload)]
    [InlineData(ExploitCapabilities.Invoke)]
    public async Task FrameworkSensitiveVerbs_AreSensitiveThroughTheirMetadata(string verb)
    {
        var registry = new InMemoryCapabilityRegistry();
        await registry.RegisterAsync(new Stub(verb));
        var policy = new CapabilityRegistrySensitiveVerbPolicy(registry);

        Assert.True(policy.IsSensitive(verb));
    }

    [Theory]
    [InlineData(CoreCapabilities.ShellExec)]
    [InlineData(CoreCapabilities.ProcKill)]         // kills-process is not unattended-sensitive
    [InlineData(CollectCapabilities.Cred)]          // reads-credential stays automatable
    [InlineData(CollectCapabilities.Screenshot)]    // reads-screen stays automatable
    [InlineData(PersistCapabilities.Install)]       // persists/writes-to-disk stay automatable
    public async Task ReferenceVerbs_StayAutomatable(string verb)
    {
        var registry = new InMemoryCapabilityRegistry();
        await registry.RegisterAsync(new Stub(verb));
        var policy = new CapabilityRegistrySensitiveVerbPolicy(registry);

        Assert.False(policy.IsSensitive(verb));
    }

    [Fact]
    public async Task AnOutOfTreeModuleWithSensitiveMetadata_IsSensitiveWithoutAnyListEdit()
    {
        // The drift case the policy exists for: a module nobody anticipated
        // registers a verb the static floor has never heard of, and the
        // metadata alone answers.
        var policy = NewPolicy(new Declared(
            "custom.thumbstrike",
            CapabilityCategory.Collect,
            new Dictionary<string, string> { ["reads-input"] = "true" }));

        Assert.True(policy.IsSensitive("custom.thumbstrike"));
    }

    [Fact]
    public async Task AnOutOfTreeModuleWithPlainMetadata_IsNotSensitive()
    {
        var policy = NewPolicy(new Declared(
            "custom.inventory",
            CapabilityCategory.Recon,
            new Dictionary<string, string> { ["touches-filesystem"] = "true" }));

        Assert.False(policy.IsSensitive("custom.inventory"));
    }

    [Fact]
    public void UnregisteredSensitiveNamespaces_StaySensitiveThroughTheFloor()
    {
        // Nothing registered at all: the evasion/exploit namespaces and the
        // sensitive three still answer sensitive -- the floor is the floor.
        var policy = NewPolicy();

        Assert.True(policy.IsSensitive("evasion.something.new"));
        Assert.True(policy.IsSensitive("exploit.still.unregistered"));
        Assert.True(policy.IsSensitive(CollectCapabilities.Keylog));
    }

    [Fact]
    public async Task AMilderDescriptorCannotNarrowTheFloor()
    {
        // A module registers a floor-listed verb with no sensitive metadata:
        // the verb stays sensitive. The registry only widens.
        var policy = NewPolicy(new Declared(
            EvasionCapabilities.Avoid,
            CapabilityCategory.Core,
            new Dictionary<string, string>()));

        Assert.True(policy.IsSensitive(EvasionCapabilities.Avoid));
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
        => Assert.True(NewPolicy().IsSensitive("COLLECT.KEYLOG"));

    private sealed class Stub : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }

        public Stub(string verb)
            => Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Core, "1.0");
    }

    private sealed class Declared : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }

        public Declared(
            string verb,
            CapabilityCategory category,
            IReadOnlyDictionary<string, string> attributes)
            => Descriptor = new CapabilityDescriptor(verb, category, "1.0", attributes);
    }
}
