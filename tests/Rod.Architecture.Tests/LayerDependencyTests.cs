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
/// operators and tradecraft depend on core state and audit. Protocol types never
/// leak into core. Adding a forbidden reference must fail one of these tests.
/// </summary>
public class LayerDependencyTests
{
    private static readonly Assembly CoreState = typeof(CoreStateLayer).Assembly;
    private static readonly Assembly Audit = typeof(AuditLayer).Assembly;
    private static readonly Assembly Transport = typeof(TransportLayer).Assembly;
    private static readonly Assembly BuildPipeline = typeof(BuildPipelineLayer).Assembly;
    private static readonly Assembly Operators = typeof(OperatorsLayer).Assembly;
    private static readonly Assembly Tradecraft = typeof(TradecraftLayer).Assembly;

    private static void AssertNoDependencies(Assembly layer, string layerName)
    {
        var result = Types.InAssembly(layer)
            .Should()
            .NotHaveDependencyOnAny("Rod.Audit", "Rod.BuildPipeline", "Rod.CoreState",
                "Rod.Operators", "Rod.Protocol", "Rod.Tradecraft", "Rod.Transport")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"{layerName} must depend on nothing in-house, but has: " +
            $"{string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    // Core state and audit are the inner ring: no in-house dependencies at all.
    [Fact]
    public void CoreState_Dependencies_PointInwardOnly()
        => AssertNoDependencies(CoreState, nameof(CoreState));

    [Fact]
    public void Audit_Dependencies_PointInwardOnly()
        => AssertNoDependencies(Audit, nameof(Audit));

    // Transport and build pipeline may depend on core state only.
    [Fact]
    public void Transport_Dependencies_PointInwardOnly()
        => AssertOnlyDependsOn(Transport, nameof(Transport), "Rod.CoreState");

    [Fact]
    public void BuildPipeline_Dependencies_PointInwardOnly()
        => AssertOnlyDependsOn(BuildPipeline, nameof(BuildPipeline), "Rod.CoreState");

    // Operators and tradecraft may depend on core state and audit only.
    [Fact]
    public void Operators_Dependencies_PointInwardOnly()
        => AssertOnlyDependsOn(Operators, nameof(Operators), "Rod.CoreState", "Rod.Audit");

    [Fact]
    public void Tradecraft_Dependencies_PointInwardOnly()
        => AssertOnlyDependsOn(Tradecraft, nameof(Tradecraft), "Rod.CoreState", "Rod.Audit");

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
        params string[] allowed)
    {
        // Forbid every in-house layer except those explicitly allowed.
        var forbidden = new[]
        {
            "Rod.Audit", "Rod.BuildPipeline", "Rod.CoreState",
            "Rod.Operators", "Rod.Protocol", "Rod.Tradecraft", "Rod.Transport"
        }.Except(allowed);

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
