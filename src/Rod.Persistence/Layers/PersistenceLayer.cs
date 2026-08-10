namespace Rod.Persistence.Layers;

/// <summary>
/// Marker class for the persistence layer (architecture.md Sec 4.1; ADR 0003).
/// Exists so the architecture tests have a type to anchor dependency-rule checks
/// to, the same role the other layer markers (CoreStateLayer, AuditLayer, ...)
/// play.
/// </summary>
public sealed class PersistenceLayer
{
}
