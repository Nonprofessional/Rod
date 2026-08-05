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

    /// <summary>
    /// A payload was built; the event carries the build's class and config and,
    /// as its outcome, the artifact's SHA-256 fingerprint (architecture.md Sec 6
    /// -- every generated artifact is fingerprinted and recorded). Emitted on
    /// every successful build. No implant is enrolled yet at build time, so the
    /// event's implant/task ids are unused.
    /// </summary>
    PayloadBuilt,

    /// <summary>
    /// An implant was retired (architecture.md Sec 7, M4.4). The event carries
    /// the implant id and the retiring operator; the outcome is the recorded
    /// retirement timestamp. A retired implant is refused at handshake and
    /// untaskable thereafter. The event has no task -- retirement is an
    /// operator action on the implant, not a task it ran.
    /// </summary>
    ImplantRetired,
}
