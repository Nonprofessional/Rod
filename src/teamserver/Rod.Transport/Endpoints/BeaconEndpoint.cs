using System.Collections.Concurrent;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Pki;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;
using Rod.Transport.Channels;
using Rod.V1;
// The domain entity shares its name with the BCL Task. This file
// uses Rod.CoreState.Tasks for the TaskService type but never
// the Task entity by name, so pin Task to the BCL type the method signatures need.
using Task = System.Threading.Tasks.Task;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The implant-initiated beacon stream: the gRPC shape of the CheckIn contract.
/// An implant opens a long-lived reverse connection; the first frame it sends is
/// the handshake (payload = <see cref="HandshakeRequest"/>), and the first frame
/// the server writes back is the <see cref="HandshakeResponse"/>. On a successful
/// handshake the implant opens a session in its engagement and the stream becomes
/// the tasking channel: the server pushes queued tasks (<see cref="TaskRequest"/>)
/// downstream and captures the implant's results (<see cref="TaskResult"/>)
/// upstream, writing each completed task to the audit trail. The same channel
/// also carries <see cref="ExfilChunk"/> frames when an implant streams an
/// artifact off the target; the server reassembles those into the
/// engagement-scoped artifact store. When the stream closes the session stays
/// live -- liveness is last-seen based, and the staleness sweeper is the close
/// path (architecture.md Sec 10.3).
///
/// The per-frame ingest (results, exfil, staged pulls, channel output) and the
/// downstream marshal (the signed TaskRequest, its audit record, staged chunk
/// runs) are shared with the plain-HTTP envelope check-in in
/// <see cref="BeaconIngest"/> and <see cref="BeaconTasking"/> -- the transport
/// changes, the frame paths do not (architecture.md Sec 8).
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
    private readonly IAuditStore _audit;
    private readonly TimeProvider _clock;
    private readonly ITaskDispatchWake _wake;
    private readonly LiveChannelHub _channels;
    private readonly TaskRelayHub _relays;
    private readonly BeaconIngest _ingest;
    private readonly BeaconTasking _tasking;

    public BeaconEndpoint(
        HandshakeService handshake,
        ISessionRegistry sessions,
        TaskService tasks,
        IAuditStore audit,
        TimeProvider clock,
        ITaskDispatchWake wake,
        LiveChannelHub channels,
        TaskRelayHub relays,
        BeaconIngest ingest,
        BeaconTasking tasking)
    {
        _handshake = handshake;
        _sessions = sessions;
        _tasks = tasks;
        _audit = audit;
        _clock = clock;
        _wake = wake;
        _channels = channels;
        _relays = relays;
        _ingest = ingest;
        _tasking = tasking;
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
            await WriteHandshakeAsync(responseStream,
                Response(HandshakeStatus.Unspecified, engagementId: null, replayNonces: false));
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
        // it open, draining results and pushing queued tasks. The stream
        // ending does NOT close the session: a session is the implant's live
        // channel, not one TCP connection -- a poll-mode implant ends every
        // check-in stream and opens the next seconds later. Liveness is
        // last-seen based; the staleness sweeper closes the session after the
        // configured silence threshold, and retirement closes it immediately.
        // The session context (engagement/implant/operator) is threaded down
        // so the frame handler can attribute exfil chunks without re-deriving
        // it from each task record.
        var sessionContext = new BeaconSessionContext(
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
        BeaconSessionContext session,
        IAsyncStreamReader<Frame> requestStream,
        IServerStreamWriter<Frame> responseStream,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // This stream's connection share of the shared frame ingest: the exfil
        // reassembly buffers and channel decoders live and die with the stream.
        var connection = _ingest.OpenConnection();
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
        var reader = ReadResultsAsync(session, connection, requestStream, pulls, linked);
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

        // The stream is gone, and a channel is session-scoped (architecture.md
        // Sec 10.3): any relay bridged onto this implant's channels dies with
        // it, so the operator-side tool's connection ends instead of staring
        // at a listener nothing more will cross.
        _relays.CloseImplant(session.Implant, "the implant's beacon stream ended");
    }

    // Reader: await each upstream frame, capture it into the task and append the
    // audit event, or -- when the frame is an ExfilChunk -- reassemble and store
    // the artifact. Each frame also advances the session's last-seen stamp, so
    // the presence roster reflects real activity, not just stream open/close.
    // Ends on a clean client close (MoveNext returns false); throws on an abort.
    private async Task ReadResultsAsync(
        BeaconSessionContext session,
        BeaconConnectionIngest connection,
        IAsyncStreamReader<Frame> requestStream,
        ConcurrentQueue<Guid> stagedPulls,
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

            await connection.IngestAsync(
                session,
                requestStream.Current,
                stagedPullSink: taskId =>
                {
                    stagedPulls.Enqueue(taskId.Value);
                    _wake.Release(session.Implant);
                },
                cancellationToken);
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
        BeaconSessionContext session,
        ConcurrentQueue<Guid> stagedPulls,
        BeaconChannelSink inputs,
        IServerStreamWriter<Frame> responseStream,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await DispatchNextAsync(session.Implant, responseStream, cancellationToken);
            await StreamStagedPullsAsync(stagedPulls, responseStream, cancellationToken);
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

    // Pulls the next queued task for the implant -- widened to the Pivot
    // children it fronts (architecture.md Sec 5.2): a fronted child's task is
    // claimed here, marked with the child's id on the frame, and executed by
    // this stream on the child's behalf. A no-op write when nothing is queued.
    private async Task DispatchNextAsync(
        ImplantId implant,
        IServerStreamWriter<Frame> responseStream,
        CancellationToken cancellationToken)
    {
        var dispatched = await _tasks.DispatchNextAsync(
            implant, cancellationToken, includeFronted: true);
        if (dispatched is null)
            return;

        var frame = _tasking.MarshalFrame(dispatched, implant);

        // Write downstream first: the dispatch audit records a task the implant
        // actually received. When the write fails, the task returns to the queue
        // so a later check-in redelivers it -- a task whose frame never left the
        // server must not strand in Dispatched (architecture.md Sec 10.3).
        try
        {
            await responseStream.WriteAsync(frame);
        }
        catch
        {
            await _tasks.RequeueAsync(dispatched.TaskId, CancellationToken.None);
            throw;
        }

        await _tasking.RecordDispatchAsync(dispatched, cancellationToken);
    }

    // Streams every demanded staged payload downstream, one StagedChunk run
    // per demand (architecture.md Sec 10, the typed arm).
    private async Task StreamStagedPullsAsync(
        ConcurrentQueue<Guid> stagedPulls,
        IServerStreamWriter<Frame> responseStream,
        CancellationToken cancellationToken)
    {
        while (stagedPulls.TryDequeue(out var taskIdValue))
        {
            foreach (var frame in await _tasking.StagedChunkRunAsync(taskIdValue, cancellationToken))
                await responseStream.WriteAsync(frame, cancellationToken);
        }
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
                    CertificateEngagementId: certIdentity?.EngagementId,
                    ReplayNonces: request.ReplayNonces),
                CancellationToken.None);

            // The full result is returned (not just the session id) so CheckIn can
            // compose the SessionOpened audit write from it -- a handshake is
            // implant-initiated, so the event is attributed to the implant's
            // DeployedBy and needs the engagement/implant/session ids the result
            // carries (architecture.md Sec 11). The replay-nonce state rides
            // the response echo so the implant knows its verification posture.
            return (Response(HandshakeStatus.Ok, result.EngagementId.ToString(), result.ReplayNonces), result);
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
            return (Response(status, engagementId: null, replayNonces: false), Handshake: null);
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

    private static HandshakeResponse Response(HandshakeStatus status, string? engagementId, bool replayNonces)
        => new()
        {
            Status = status,
            Version = new ProtocolVersion { Major = ProtocolVersions.Major, Minor = ProtocolVersions.Minor },
            EngagementId = engagementId ?? string.Empty,
            ReplayNonces = replayNonces,
        };

    private static Task WriteHandshakeAsync(IServerStreamWriter<Frame> stream, HandshakeResponse response)
    {
        var frame = new Frame { Payload = ByteString.CopyFrom(response.ToByteArray()) };
        return stream.WriteAsync(frame);
    }
}
