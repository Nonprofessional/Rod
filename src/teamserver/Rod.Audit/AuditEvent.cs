namespace Rod.Audit;

/// <summary>
/// An immutable, attributed operational fact (architecture.md Sec 11). Every
/// privileged action produces one: who (<see cref="OperatorId"/>), where
/// (<see cref="EngagementId"/>), against what (<see cref="ImplantId"/>/
/// <see cref="TaskId"/>), what verb (<see cref="Verb"/>), when (<see cref="At"/>),
/// and -- for task events -- the captured <see cref="Output"/> and
/// <see cref="Outcome"/>. This is the engagement timeline by construction.
///
/// Events are hash-chained per engagement (storage &amp; audit layer, roadmap
/// M2.3): <see cref="PreviousHash"/> is the hash of the previous event in the
/// same engagement (the genesis all-zero hash for the first event), and
/// <see cref="Hash"/> is this event's hash, taken over its contents together
/// with <see cref="PreviousHash"/>. Each event therefore commits to its
/// predecessor, so tampering with a stored event breaks the chain at the next
/// link (see <see cref="AuditChain"/>). Callers build the facts with
/// <see cref="Fact"/>; the store stamps both hashes on append.
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
    DateTimeOffset At,
    string PreviousHash,
    string Hash)
{
    /// <summary>
    /// Builds the pre-chain fact: the audited action's contents, before the store
    /// has stamped it onto an engagement's chain. Callers (a transport endpoint
    /// capturing a task result, a use case emitting an audit point) use this; the
    /// <see cref="IAuditStore"/> replaces it with a chained copy on append, so the
    /// hash fields never have to be supplied at the call site.
    /// </summary>
    public static AuditEvent Fact(
        Guid eventId,
        Guid engagementId,
        Guid operatorId,
        Guid implantId,
        Guid taskId,
        string verb,
        AuditEventKind kind,
        string payload,
        string? output,
        string outcome,
        DateTimeOffset at)
        => new(
            eventId,
            engagementId,
            operatorId,
            implantId,
            taskId,
            verb,
            kind,
            payload,
            output,
            outcome,
            at,
            PreviousHash: string.Empty,
            Hash: string.Empty);
}
