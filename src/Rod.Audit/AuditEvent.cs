namespace Rod.Audit;

/// <summary>
/// An immutable, attributed operational fact (architecture.md Sec 11). Every
/// privileged action produces one: who (<see cref="OperatorId"/>), where
/// (<see cref="EngagementId"/>), against what (<see cref="ImplantId"/>/
/// <see cref="TaskId"/>), what verb (<see cref="Verb"/>), when (<see cref="At"/>),
/// and -- for task events -- the captured <see cref="Output"/> and
/// <see cref="Outcome"/>. This is the engagement timeline by construction.
///
/// Minimal shape for the walking skeleton (roadmap M1.4): the event carries the
/// facts and is append-only, but it is not yet hash-chained. The full
/// append-only, hash-chained store -- where tampering breaks the chain -- arrives
/// with the storage &amp; audit layer (roadmap M2.3); this type grows the
/// <c>PreviousHash</c>/<c>Hash</c> fields then, in place.
/// </summary>
public sealed record AuditEvent(
    Guid EventId,
    Guid EngagementId,
    Guid OperatorId,
    Guid ImplantId,
    Guid TaskId,
    string Verb,
    AuditEventKind Kind,
    string Payload,
    string? Output,
    string Outcome,
    DateTimeOffset At);
