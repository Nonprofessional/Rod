namespace Rod.Capabilities.Layers;

/// <summary>
/// Marker class for the Pluggable Capabilities layer (architecture.md Sec 4.1).
/// Exists so the architecture tests have a type to anchor dependency-rule checks
/// to. The layer's core -- the capability-module contract, registry, and
/// task resolver (architecture.md Sec 10/13) -- lives alongside this marker; this
/// type stays as the dependency-rule anchor regardless of how much real code the
/// layer grows.
/// </summary>
public sealed class CapabilitiesLayer
{
}
