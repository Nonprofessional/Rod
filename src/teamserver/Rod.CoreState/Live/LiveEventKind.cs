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
}
