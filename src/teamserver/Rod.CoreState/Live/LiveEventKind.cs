namespace Rod.CoreState.Live;

/// <summary>
/// The kinds of operational change the operator layer pushes live to connected
/// operator sessions (architecture.md Sec 4.1, layer 4). These are the
/// realtime fan-out events -- the durable, attributed record of every action
/// still lives in the audit trail (Sec 11); the bus is best-effort and rebuilds
/// its projection from current state on reconnect.
/// </summary>
public enum LiveEventKind
{
    /// <summary>
    /// An operator session opened on the engagement. Carries the operator id and
    /// handle so peers can render "who is online" without an extra round-trip.
    /// </summary>
    OperatorJoined,

    /// <summary>An operator session closed on the engagement.</summary>
    OperatorLeft,

    /// <summary>
    /// A task was issued against an implant in the engagement. Lets every
    /// connected operator see tasking the moment it is queued, attributed to the
    /// issuing operator.
    /// </summary>
    TaskIssued,

    /// <summary>
    /// A task completed -- the implant returned a result. The captured outcome
    /// reaches every operator session in real time.
    /// </summary>
    TaskCompleted,

    /// <summary>
    /// A queued task was retracted before dispatch (architecture.md Sec 10.3).
    /// Lets every connected operator see the tasking leave the queue the moment
    /// it is cancelled, so a peer's queued view does not linger.
    /// </summary>
    TaskCancelled,

    /// <summary>
    /// A streaming task's channel produced output (architecture.md Sec 10.3,
    /// the streaming task shape). The chunk reaches every connected operator
    /// session as it streams, so a live channel reads like a terminal; the
    /// task's accumulating transcript remains the durable record.
    /// </summary>
    ChannelOutput,

    /// <summary>
    /// An implant was retired (architecture.md Sec 7). Lets connected
    /// operators see an implant leave the live fleet the moment it is taken out
    /// of operation, rather than waiting for it to drop off presence on its
    /// next (refused) handshake.
    /// </summary>
    ImplantRetired,

    /// <summary>
    /// A session was closed by the staleness sweep: its beacon stream stopped
    /// producing frames longer than the configured threshold (architecture.md
    /// Sec 10.3). System-initiated, so it carries the null operator
    /// (<c>OperatorId.Empty</c>); connected operators refresh the roster on it,
    /// seeing the implant drop offline without polling.
    /// </summary>
    SessionClosed,

    /// <summary>
    /// An implant opened a session -- it contacted and came online
    /// (architecture.md Sec 10.3). Fires only for a genuinely new session: the
    /// registry reuses the active session on a poll contact or a flapped
    /// stream, and a contact cadence must not flood the stream. Connected
    /// operators refresh the online roster on it, seeing the implant appear
    /// without waiting out a poll -- the roster's mirror of
    /// <see cref="SessionClosed"/>.
    /// </summary>
    SessionOpened,

    /// <summary>
    /// A shellcatch listener accepted a reverse-shell connection
    /// (architecture.md Sec 8). Connected operators refresh the shell roster
    /// on it -- the shell-facing mirror of <see cref="SessionOpened"/>. The
    /// payload carries the shell session id, remote address, and listener;
    /// output itself is not fanned out per chunk (the console reads the
    /// shell's output log directly), so the stream stays roster-paced.
    /// </summary>
    ShellSessionOpened,

    /// <summary>
    /// A caught shell left the live roster -- lost to the network or closed
    /// by an operator (architecture.md Sec 8). The shell-facing mirror of
    /// <see cref="SessionClosed"/>; the payload carries the shell session id
    /// and how it ended.
    /// </summary>
    ShellSessionEnded,

    /// <summary>
    /// An artifact was fetched over an engagement web front's delivery route
    /// (architecture.md Sec 6): a launcher credential was spent by an actual
    /// download. Connected operators refresh their kept-launcher rows on it,
    /// seeing the remaining budget move the moment a target pulls it -- without
    /// waiting for the enrollment that may follow (and may never come: a
    /// burned one-liner pulled by a scanner never enrolls). System-initiated
    /// (the fetch), so it carries the null operator; the payload names the
    /// fetcher's address and user agent.
    /// </summary>
    PayloadFetched,
}
