namespace Rod.Audit;

/// <summary>
/// What kind of operational fact an <see cref="AuditEvent"/> records
/// (architecture.md Sec 11). Every per-engagement action that changes state or
/// binds an identity produces exactly one kind: the engagement's own creation, a
/// stager token mint, an implant enrollment, a session opening, a task's
/// issuance/dispatch/completion, a payload build, an implant's retirement, an
/// evidence artifact attached to a task, and an artifact the implant itself
/// exfiltrated over the beacon stream. Together they form the engagement
/// timeline -- the attributed, append-only, hash-chained event stream the
/// acceptance point calls for.
/// </summary>
public enum AuditEventKind
{
    /// <summary>
    /// An operator created an engagement (architecture.md Sec 3).
    /// The first event in any engagement's trail. The event carries the
    /// engagement name in its payload and the new engagement id as its outcome;
    /// it is attributed to the creating owner. The chain's genesis link.
    /// </summary>
    EngagementCreated,

    /// <summary>
    /// An operator minted a stager token for an engagement. The payload carries
    /// the token's bounded-use/expiry shape; the outcome is
    /// the new token id. The secret itself is never recorded -- only the fact
    /// that a token was minted, by whom, and against which engagement.
    /// </summary>
    StagerTokenMinted,

    /// <summary>
    /// A stager token was redeemed and an implant enrolled into its engagement.
    /// The payload carries the implant's class (and the parent when it is a
    /// child derivation, architecture.md Sec 5.2); the
    /// outcome is the new implant id. Enrollment is implant-initiated, so the
    /// event is attributed to the operator who minted the redeemed token -- the
    /// one who authorized the deployment -- carried on the implant as
    /// <c>DeployedBy</c>.
    /// </summary>
    ImplantEnrolled,

    /// <summary>
    /// An implant opened a session on a successful handshake. The payload
    /// carries the negotiated protocol version; the outcome is the
    /// session id. As with enrollment the actor is the implant, but the event is
    /// attributed to the operator who deployed it (the token issuer), so the
    /// "an implant came online" fact is bound to an accountable operator.
    /// </summary>
    SessionOpened,

    /// <summary>
    /// An operator issued a task against an implant. The event carries the verb
    /// and arguments in its payload and the new task id
    /// as its outcome. Issuance is the operator's intent; <see cref="TaskDispatched"/>
    /// records the server handing the task to the implant, and
    /// <see cref="TaskCompleted"/> records the result. A task's full attributed
    /// arc is these three events.
    /// </summary>
    TaskIssued,

    /// <summary>
    /// A queued task was handed to an implant on its beacon stream. The payload carries the verb and arguments; the outcome is
    /// the dispatched task id. Dispatch is server-driven (the implant pulls the
    /// queue), so the event is attributed to the operator who issued the task --
    /// the one whose tasking the dispatch carries out.
    /// </summary>
    TaskDispatched,

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
    /// An implant was retired (architecture.md Sec 7). The event carries
    /// the implant id and the retiring operator; the outcome is the recorded
    /// retirement timestamp. A retired implant is refused at handshake and
    /// untaskable thereafter. The event has no task -- retirement is an
    /// operator action on the implant, not a task it ran.
    /// </summary>
    ImplantRetired,

    /// <summary>
    /// An operator attached an evidence artifact to a task (architecture.md
    /// Sec 11). Artifacts -- files, screenshots, captured command
    /// output -- are first-class objects linked to the task that gathered them,
    /// not loose files; this event records the binding. The payload carries the
    /// artifact's name and content type, and the outcome is the new artifact id.
    /// The event is attributed to the attaching operator and carries the task it
    /// was bound to.
    /// </summary>
    ArtifactAttached,

    /// <summary>
    /// An implant streamed an artifact to the teamserver over the beacon stream
    /// (architecture.md Sec 10.1 exfil, Sec 11). Unlike <see cref="ArtifactAttached"/>,
    /// which records an operator binding a file it already holds, this records
    /// the implant itself exfiltrating bytes off the target as ExfilChunk
    /// frames; the server reassembles the chunks into an artifact scoped to the
    /// engagement and bound to the task that triggered the push. The payload
    /// carries the artifact's name and content type, and the outcome is the new
    /// artifact id. The event is attributed to the implant (via the task's
    /// <c>IssuedBy</c>) and carries the task the push was bound to.
    /// </summary>
    ExfilCaptured,

    /// <summary>
    /// An operator applied an engagement's rules-of-engagement profile
    /// (architecture.md Sec 9 -- ROE guardrails). The payload carries the
    /// profile's shape (permitted verbs and targets; empty lists are the
    /// unrestricted scope) and the outcome is the engagement id. Every later
    /// refusal the profile causes is a <see cref="TaskRoeRefused"/> event, so
    /// this record is what the trail shows the scope in force at any moment.
    /// </summary>
    RoeUpdated,

    /// <summary>
    /// A task issuance was refused by the engagement's rules-of-engagement
    /// profile before it was queued (architecture.md Sec 9). The payload
    /// carries the verb and arguments that were refused, and the outcome names
    /// the violated rule -- which verb or target was outside the engagement's
    /// permitted scope. The event is attributed to the issuing operator; the
    /// task never exists, so it carries no task id.
    /// </summary>
    TaskRoeRefused,

    /// <summary>
    /// An operator sent input to a live task channel (architecture.md Sec
    /// 10.3, the streaming task shape). The payload carries the decoded input
    /// (or the eof marker when the operator closed the channel's stdin); the
    /// event is attributed to the sending operator and bound to the channel's
    /// task. What the channel streamed back rides the task's
    /// <see cref="TaskCompleted"/> event as its transcript.
    /// </summary>
    ChannelInput,
}
