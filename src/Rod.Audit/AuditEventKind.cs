namespace Rod.Audit;

/// <summary>
/// What kind of operational fact an <see cref="AuditEvent"/> records
/// (architecture.md Sec 11). The walking skeleton (roadmap M1.4) emits only
/// <see cref="TaskCompleted"/>: capturing a task's result is the first audited
/// action. More kinds arrive with the storage &amp; audit layer (roadmap M2.3)
/// -- enrollment, handshake, dispatch, and sensitive-verb guardrails each become
/// their own event.
/// </summary>
public enum AuditEventKind
{
    /// <summary>
    /// An implant returned a task result; the event carries the verb, the
    /// captured output, and the outcome. Emitted on every completed task.
    /// </summary>
    TaskCompleted,
}
