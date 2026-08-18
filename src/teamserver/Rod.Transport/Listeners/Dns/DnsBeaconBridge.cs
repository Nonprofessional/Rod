using System.Collections.Concurrent;
using Google.Protobuf;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Live;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;
using Rod.Transport.Endpoints;
// The domain entity shares its name with the BCL Task; this file uses the
// Rod.CoreState.Tasks types (TaskId, TaskOutcome, TaskCompleted) but never the
// entity by name, so pin Task to the BCL type the signatures need.
using Task = System.Threading.Tasks.Task;

namespace Rod.Transport.Listeners.Dns;

// The DNS check-in bridge (architecture.md Sec 8): maps the datagram-shaped
// DNS contract onto the same core-state machinery the beacon stream uses --
// presence through the session registry, tasking through TaskService with the
// CA's command signature, results through the capture-and-audit composition
// the beacon endpoint performs. DNS carries no handshake and no mTLS: the
// transport assumes the implant's session was opened on a handshake-capable
// transport and refreshes it; identity on the wire is the implant id alone
// (the egress-restricted tradeoff, documented in extending/implants.md).
// Downstream tasking stays tamper-evident regardless: the TaskRequest carries
// the same RSASSA-PSS signature over the canonical tuple (architecture.md
// Sec 9), and a DNS-delivered task verifies exactly like a stream-delivered
// one.

/// <summary>
/// Serves poll and result check-ins for one teamserver. Singleton: the UDP
/// listener services (one per DNS listener entry) share it.
/// </summary>
internal sealed class DnsBeaconBridge
{
    // The marshaled-TaskRequest budget for one DNS answer: the signed
    // TaskRequest must fit the EDNS0 response (1232 bytes) with the DNS
    // headers and the base32 expansion (5/8). A task whose arguments push it
    // over is requeued untouched -- DNS carries short-argument tasking, and
    // the queue keeps the task for a stream transport to claim.
    public const int MaxTaskRequestBytes = 560;

    private readonly ISessionRegistry _sessions;
    private readonly TaskService _tasks;
    private readonly IAuditStore _audit;
    private readonly ILiveEventBus _bus;
    private readonly TimeProvider _clock;
    private readonly BeaconTasking _tasking;
    private readonly DnsCheckInNames.ResultReassembler _results = new();

    public DnsBeaconBridge(
        ISessionRegistry sessions,
        TaskService tasks,
        IAuditStore audit,
        ILiveEventBus bus,
        TimeProvider clock,
        BeaconTasking tasking)
    {
        _sessions = sessions;
        _tasks = tasks;
        _audit = audit;
        _bus = bus;
        _clock = clock;
        _tasking = tasking;
    }

    /// <summary>
    /// One poll: refreshes the implant's presence and hands back the next
    /// queued task as marshaled, signed TaskRequest bytes -- or null when
    /// there is nothing to send (no live session, empty queue, a channel task
    /// only a stream can run, or a task too large for the DNS budget; the
    /// latter two are requeued for a stream transport).
    /// </summary>
    public async Task<byte[]?> PollAsync(ImplantId implant, CancellationToken cancellationToken)
    {
        // DNS carries no handshake: presence only refreshes a session another
        // transport opened. An implant with no active session is not present
        // as far as this listener is concerned.
        var session = await _sessions.GetActiveAsync(implant, cancellationToken);
        if (session is null)
            return null;

        // Re-touch with the session's own capabilities: the touch replaces
        // them, and a DNS check-in carries no advertisement of its own.
        await _sessions.TouchAsync(implant, session.Capabilities, _clock.GetUtcNow(), cancellationToken);

        var dispatched = await _tasks.DispatchNextAsync(implant, cancellationToken);
        if (dispatched is null)
            return null;

        // A streaming task is a channel on the beacon stream that carried it
        // (architecture.md Sec 10.3): its input rides ChannelInput frames and
        // its output streams back as ChannelOutput. A datagram poll has no
        // stream to run a channel on, so a channel task is requeued untouched
        // for a stream transport to claim -- the same handback an oversized
        // task gets below.
        if (ChannelVerbs.IsChannelVerb(dispatched.Verb))
        {
            await _tasks.RequeueAsync(dispatched.TaskId, CancellationToken.None);
            return null;
        }

        var marshaled = _tasking.BuildSignedRequest(dispatched).ToByteArray();
        if (marshaled.Length > MaxTaskRequestBytes)
        {
            // Too big for a datagram answer: hand it back to the queue so a
            // stream transport claims it (architecture.md Sec 10.3).
            await _tasks.RequeueAsync(dispatched.TaskId, CancellationToken.None);
            return null;
        }

        // The dispatch is recorded with the same shape every beacon transport
        // writes (architecture.md Sec 11): attributed to the operator whose
        // tasking it carries out, the outcome the dispatched task id.
        await _tasking.RecordDispatchAsync(dispatched, cancellationToken);
        return marshaled;
    }

    /// <summary>
    /// One result chunk: reassembles (the bounded buffer in
    /// <see cref="DnsCheckInNames"/>); on the terminal chunk, captures the
    /// outcome into the task with the same audit and live-event composition
    /// the beacon stream performs. The implant is attributed by its id -- the
    /// DNS tradeoff -- and a result for an implant other than the task's own
    /// is dropped.
    /// </summary>
    public async Task ResultChunkAsync(
        ImplantId implant,
        TaskId task,
        Rod.CoreState.Tasks.TaskOutcome outcome,
        int sequence,
        bool terminal,
        byte[] chunk,
        CancellationToken cancellationToken)
    {
        // Presence rides the result path too.
        var session = await _sessions.GetActiveAsync(implant, cancellationToken);
        if (session is null)
            return;
        await _sessions.TouchAsync(implant, session.Capabilities, _clock.GetUtcNow(), cancellationToken);

        // The terminal chunk closes the reassembly; null means keep buffering
        // (more chunks) or drop (a gap in the sequence).
        var output = _results.Add(task, sequence, terminal, chunk);
        if (output is null)
            return;

        TaskCompleted completed;
        try
        {
            completed = await _tasks.RecordResultAsync(task, System.Text.Encoding.UTF8.GetString(output), outcome, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Unknown task or a retransmitted result after completion: ignore,
            // the same tolerance the beacon stream shows.
            return;
        }
        if (completed.ImplantId != implant)
            return; // a result naming another implant's task: drop

        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: completed.EngagementId.Value,
                operatorId: completed.IssuedBy.Value,
                implantId: completed.ImplantId.Value,
                taskId: completed.TaskId.Value,
                verb: completed.Verb,
                kind: AuditEventKind.TaskCompleted,
                payload: completed.Arguments,
                output: completed.Output,
                outcome: completed.Outcome.ToString(),
                at: completed.CompletedAt),
            cancellationToken);
        await _bus.PublishAsync(
            LiveEvent.TaskCompleted(
                completed.EngagementId,
                completed.IssuedBy,
                completed.ImplantId,
                completed.TaskId,
                payload: $"{completed.Outcome}: {completed.Output}",
                completed.CompletedAt),
            cancellationToken);
    }
}
