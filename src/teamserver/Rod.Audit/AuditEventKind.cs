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
    /// An operator retracted a queued task before the implant woke
    /// (architecture.md Sec 10.3). The event carries the retracted verb and
    /// arguments in its payload; the outcome is the recorded cancellation
    /// timestamp. Attributed to the cancelling operator and bound to the task
    /// it retracts. A cancelled task's arc ends here: no
    /// <see cref="TaskDispatched"/> follows, because the retraction is
    /// claim-proof against the dispatch queue.
    /// </summary>
    TaskCancelled,

    /// <summary>
    /// A payload was built; the event carries the build's class and config and,
    /// as its outcome, the artifact's SHA-256 fingerprint (architecture.md Sec 6
    /// -- every generated artifact is fingerprinted and recorded). Emitted on
    /// every successful build. No implant is enrolled yet at build time, so the
    /// event's implant/task ids are unused.
    /// </summary>
    PayloadBuilt,

    /// <summary>
    /// A stored payload was deleted from the library by an operator: the bytes
    /// and the listing entry are gone, and a stager fetching it 404s from now
    /// on -- the kill switch for a hosted stage-2. The event carries the
    /// payload's class and target with the fingerprint it carried in life, so
    /// the trail still names what was removed even though the bytes are not
    /// retrievable anymore.
    /// </summary>
    PayloadDeleted,

    /// <summary>
    /// An implant was retired (architecture.md Sec 7). The event carries
    /// the implant id and the retiring operator; the outcome is the recorded
    /// retirement timestamp. A retired implant is refused at handshake and
    /// untaskable thereafter. The event has no task -- retirement is an
    /// operator action on the implant, not a task it ran.
    /// </summary>
    ImplantRetired,

    /// <summary>
    /// An operator wrote a free-text note on an implant -- the "whose beacon
    /// is this" memory. The note is recorded as this event (the payload is the
    /// note text, the outcome "added"), attributed to the writing operator and
    /// bound to the implant it describes; notes read back from the trail, so
    /// they survive a teamserver restart the same way every engagement fact
    /// does, with no separate note store to keep consistent. The event has no
    /// task -- a note annotates the implant, it does not task it.
    /// </summary>
    ImplantNoteAdded,

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

    /// <summary>
    /// An operator bound a relay port onto a live tunnel channel
    /// (architecture.md Sec 10.1 tunnel, Sec 10.3): a teamserver-side TCP
    /// listener whose accepted connection bridges into the channel, so
    /// unmodified operator tooling rides the tunnel without per-byte input
    /// posts. The payload carries the listen endpoint; the event is attributed
    /// to the binding operator and bound to the tunnel's task.
    /// <see cref="RelayClosed"/> records how the relay ended.
    /// </summary>
    RelayBound,

    /// <summary>
    /// A relay port bound onto a live tunnel channel ended
    /// (architecture.md Sec 10.1 tunnel, Sec 10.3): the task completed, the
    /// beacon stream died, the operator unbound it, or the bridged connection
    /// ended. The payload names the cause and the relayed byte tallies; the
    /// event is attributed to the operator who bound the relay. The relayed
    /// traffic itself rides the task's transcript -- the same no-per-chunk
    /// discipline the channel's own output follows.
    /// </summary>
    RelayClosed,

    /// <summary>
    /// An operator froze the engagement for close-out (architecture.md Sec 2
    /// step 10). From this event on, the engagement accepts no new tasking and
    /// no new deployments; the payload carries the freeze timestamp, and the
    /// outcome is the engagement id. The first event of the close-out arc that
    /// ends with <see cref="EvidenceExported"/> and
    /// <see cref="EngagementRetired"/>.
    /// </summary>
    EngagementFrozen,

    /// <summary>
    /// An operator exported the engagement's evidence package (architecture.md
    /// Sec 11): the hash-chained audit trail, the artifacts, and the report as
    /// one package that survives infrastructure teardown and re-verifies
    /// offline. The payload carries the package's file counts; the outcome is
    /// the exported trail's chain-head hash -- the digest that pins everything
    /// the package carries. Written after the package is built, so it is not
    /// part of the exported trail -- a later re-export carries it.
    /// </summary>
    EvidenceExported,

    /// <summary>
    /// An operator retired the engagement, completing its close-out
    /// (architecture.md Sec 2 step 10): terminal. The payload carries the
    /// retirement timestamp; the outcome is the engagement id. The trail --
    /// including this event -- remains the durable, append-only account of the
    /// engagement after its infrastructure is gone.
    /// </summary>
    EngagementRetired,

    /// <summary>
    /// An operator reversed a freeze: the engagement reopens and accepts
    /// tasking and deployments again. The recovery for a mistaken freeze,
    /// refused after retirement. The payload carries the unfreeze timestamp;
    /// the outcome is the engagement id. The freeze and this event both stay
    /// in the trail, so an exported package from the frozen window remains a
    /// verifiable snapshot of that window while the live trail tells the full
    /// story.
    /// </summary>
    EngagementUnfrozen,

    /// <summary>
    /// An operator revoked a stager token: the credential stops working at the
    /// next redeem or verify, whatever uses and validity it had left. The
    /// emergency answer to a leaked credential -- above all one baked into a
    /// deployed artifact. The payload names why revocation exists (the baked
    /// shape or the manual mint); the outcome is the revoked token id.
    /// </summary>
    StagerTokenRevoked,

    /// <summary>
    /// An operator edited the engagement's working record: its name and
    /// free-text description. The payload carries the new name (and whether a
    /// description is set); the outcome is the engagement id. The description
    /// text itself is not recorded -- it is the crew's working notes, not a
    /// fact about the target environment, and the trail records that the
    /// record moved, not every draft of it.
    /// </summary>
    EngagementUpdated,

    /// <summary>
    /// A shellcatch listener accepted a reverse-shell connection
    /// (architecture.md Sec 8). The peer speaks no Rod protocol and carries
    /// no identity, so the event is scoped by the engagement-bound listener
    /// it landed on. System-initiated (the accept), so it is attributed to
    /// the null operator. The payload carries the remote address, the
    /// listener, and the fingerprint once guessed; the outcome is the new
    /// shell session id.
    /// </summary>
    ShellSessionOpened,

    /// <summary>
    /// A caught shell session ended on its own -- the peer vanished, the
    /// shell exited, or the socket died (Lost). System-initiated, so it is
    /// attributed to the null operator; an operator's deliberate close is
    /// its own operator-attributed <see cref="ShellSessionClosed"/> event.
    /// The payload carries the last-input/output stamps; the outcome is the
    /// shell session id.
    /// </summary>
    ShellSessionEnded,

    /// <summary>
    /// An operator closed a caught shell deliberately. Operator-attributed;
    /// the outcome is the shell session id. The socket's unblock and the
    /// session's Closed marking follow from the pump honoring the close.
    /// </summary>
    ShellSessionClosed,

    /// <summary>
    /// An operator wrote input to a caught shell (architecture.md Sec 8).
    /// Operator-attributed and precise per submission, unlike output (which
    /// flows unsolicited and is summarized on the session's end events):
    /// input is the operator's own action against the target and is recorded
    /// exactly. The payload carries the submitted text; the outcome is the
    /// shell session id.
    /// </summary>
    ShellSessionInput,

    /// <summary>
    /// An operator registered a web-shell endpoint into the engagement
    /// (architecture.md Sec 5.2's Web-shell class): a script already placed
    /// in a target's web root, bound to the engagement by the register
    /// action itself. The payload carries the URL and protocol adapter; the
    /// outcome is the WebShell-class implant id the endpoint is anchored
    /// to. The connection password is not recorded -- the script that
    /// carries it lives on the target, and the trail records the fact of
    /// the binding, not the credential.
    /// </summary>
    WebShellRegistered,

    /// <summary>
    /// An operator removed a web-shell endpoint: the connection profile is
    /// gone and the anchor implant row retired with its history readable
    /// (architecture.md Sec 7's shape). The payload carries the URL; the
    /// outcome is the implant id.
    /// </summary>
    WebShellRemoved,

    /// <summary>
    /// An operator probed a web-shell endpoint -- the one-request health
    /// check that stands in for a beacon's liveness answer. The payload
    /// carries the outcome (ok, latency, or the refusal reason); the
    /// outcome is the implant id. A probe executes a marker echo on the
    /// target, so it is recorded like any other operator action.
    /// </summary>
    WebShellProbed,
}
