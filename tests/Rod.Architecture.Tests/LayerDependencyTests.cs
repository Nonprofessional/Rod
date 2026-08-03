using System.Reflection;
using NetArchTest.Rules;
using Rod.Audit.Layers;
using Rod.BuildPipeline.Layers;
using Rod.CoreState.Layers;
using Rod.Operators.Layers;
using Rod.Tradecraft.Layers;
using Rod.Transport.Layers;

namespace Rod.Architecture.Tests;

/// <summary>
/// Encodes the teamserver layer dependency rules (architecture.md Sec 4.1,
/// AGENTS.md Sec 5). Dependencies point inward only: core state and audit depend
/// on nothing in-house; transport and build pipeline depend on core state;
/// operators and tradecraft depend on core state and audit. The transport layer
/// additionally depends on Rod.Protocol: Protocol is the language-neutral wire
/// contract (architecture.md Sec 8/9) that the transport speaks, so mapping
/// outcomes to wire status codes belongs in transport. Protocol itself depends on
/// nothing in-house, and still never leaks into core state. Adding a forbidden
/// reference must fail one of these tests.
/// </summary>
public class LayerDependencyTests
{
    private static readonly Assembly CoreState = typeof(CoreStateLayer).Assembly;
    private static readonly Assembly Audit = typeof(AuditLayer).Assembly;
    private static readonly Assembly Transport = typeof(TransportLayer).Assembly;
    private static readonly Assembly BuildPipeline = typeof(BuildPipelineLayer).Assembly;
    private static readonly Assembly Operators = typeof(OperatorsLayer).Assembly;
    private static readonly Assembly Tradecraft = typeof(TradecraftLayer).Assembly;

    // Every in-house layer base namespace. A type that references any of these
    // namespaces has a dependency on that layer.
    private static readonly string[] AllLayers =
    {
        "Rod.Audit", "Rod.BuildPipeline", "Rod.CoreState",
        "Rod.Operators", "Rod.Protocol", "Rod.Tradecraft", "Rod.Transport"
    };

    // Inner ring: the layer may use only itself. Any reference to another
    // in-house layer is forbidden. The layer's own namespace is always allowed so
    // types within the layer can reference each other; without that, a layer's
    // own sub-namespaces would trip NetArchTest's namespace-based dependency
    // check the moment real code is added.
    private static void AssertNoDependencies(Assembly layer, string layerName, string ownNamespace)
    {
        var forbidden = AllLayers.Except(new[] { ownNamespace }).ToArray();

        var result = Types.InAssembly(layer)
            .Should()
            .NotHaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"{layerName} must depend on nothing in-house, but has: " +
            $"{string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    // Core state and audit are the inner ring: no in-house dependencies at all.
    [Fact]
    public void CoreState_Dependencies_PointInwardOnly()
        => AssertNoDependencies(CoreState, nameof(CoreState), "Rod.CoreState");

    [Fact]
    public void Audit_Dependencies_PointInwardOnly()
        => AssertNoDependencies(Audit, nameof(Audit), "Rod.Audit");

    // Transport may depend on core state, the wire protocol contract, audit, and
    // the build pipeline. Protocol is the language-neutral contract transport
    // speaks (architecture.md Sec 8/9); it depends on nothing in-house and never
    // leaks into core state. Audit is the innermost ring alongside core state
    // (architecture.md Sec 4.1/11): when a task result arrives, transport composes
    // the audit write itself, so it depends inward on the audit port. Build
    // pipeline (roadmap M3.1): the operator-facing build endpoint drives the build
    // orchestrator and composes the PayloadBuilt audit write, the same way the
    // beacon stream composes the task-completion write, so transport depends inward
    // on the build contract. All dependencies point inward; transport never
    // reverses them, and build pipeline still depends on core state only.
    [Fact]
    public void Transport_Dependencies_PointInwardOnly()
        => AssertOnlyDependsOn(
            Transport, nameof(Transport), "Rod.Transport",
            "Rod.CoreState", "Rod.Protocol", "Rod.Audit", "Rod.BuildPipeline");

    [Fact]
    public void BuildPipeline_Dependencies_PointInwardOnly()
        => AssertOnlyDependsOn(BuildPipeline, nameof(BuildPipeline), "Rod.BuildPipeline", "Rod.CoreState");

    // Operators and tradecraft may depend on core state and audit only.
    [Fact]
    public void Operators_Dependencies_PointInwardOnly()
        => AssertOnlyDependsOn(Operators, nameof(Operators), "Rod.Operators", "Rod.CoreState", "Rod.Audit");

    [Fact]
    public void Tradecraft_Dependencies_PointInwardOnly()
        => AssertOnlyDependsOn(Tradecraft, nameof(Tradecraft), "Rod.Tradecraft", "Rod.CoreState", "Rod.Audit");

    // Protocol types must never leak into core (AGENTS.md Sec 5).
    [Fact]
    public void Protocol_DoesNotLeak_IntoCoreState()
    {
        var result = Types.InAssembly(CoreState)
            .Should()
            .NotHaveDependencyOn("Rod.Protocol")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Protocol types leaked into core state: " +
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }

    private static void AssertOnlyDependsOn(Assembly layer, string layerName,
        string ownNamespace, params string[] allowed)
    {
        // Forbid every in-house layer except the layer's own namespace and those
        // explicitly allowed (own namespace is always allowed so the layer's
        // types can reference each other).
        var forbidden = AllLayers.Except(allowed.Append(ownNamespace));

        var result = Types.InAssembly(layer)
            .Should()
            .NotHaveDependencyOnAny(forbidden.ToArray())
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"{layerName} may only depend on {string.Join(", ", allowed)}, " +
            $"but has forbidden dependencies: " +
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }
}
