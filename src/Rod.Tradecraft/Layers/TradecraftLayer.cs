namespace Rod.Tradecraft.Layers;

/// <summary>
/// Marker class for the Pluggable Tradecraft layer (architecture.md Sec 4.1).
/// Exists so the architecture tests have a type to anchor dependency-rule checks
/// to. The layer's skeleton -- the capability-module contract, registry, and
/// dispatcher (architecture.md Sec 10/13) -- lives alongside this marker; this
/// type stays as the dependency-rule anchor regardless of how much real code the
/// layer grows.
/// </summary>
public sealed class TradecraftLayer
{
}
