using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Live;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;
using Rod.V1;
// The domain entity shares its name with System.Threading.Tasks.Task. This file
// uses Rod.CoreState.Tasks for the TaskId/TaskOutcome/TaskService types but never
// the Task entity by name, so pin Task to the BCL type the method signatures need.
using Task = System.Threading.Tasks.Task;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The implant-initiated beacon stream (roadmap M1.3, tasking added M1.4). An
/// implant opens a long-lived reverse connection; the first frame it sends is the
/// handshake (payload = <see cref="HandshakeRequest"/>), and the first frame the
/// server writes back is the <see cref="HandshakeResponse"/>. On a successful
/// handshake the implant opens a session in its engagement and the stream
/// becomes the tasking channel: the server pushes queued tasks
/// (<see cref="TaskRequest"/>) downstream and captures the implant's results
/// (<see cref="TaskResult"/>) upstream, writing each completed task to the audit
/// trail. When the stream closes the session is closed.
///
/// When a result is captured the stream also publishes a
/// <see cref="LiveEventKind.TaskCompleted"/> event on the live bus (roadmap
/// M2.4), so every connected operator session sees the outcome in real time;
/// the audit write is the durable record, the live event the transient fan-out.
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
    private readonly ILiveEventBus _bus;

    public BeaconEndpoint(
        HandshakeService handshake,
        ISessionRegistry sessions,
        TaskService tasks,
        IAuditStore audit,
        ILiveEventBus bus)
    {
        _handshake = handshake;
        _sessions = sessions;
        _tasks = tasks;
        _audit = audit;
        _bus = bus;
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
        var (response, session) = await TryHandshakeAsync(httpContext, handshakeRequest);
        await WriteHandshakeAsync(responseStream, response);
        if (response.Status != HandshakeStatus.Ok || session is null)
            return;

        var implant = ResolveImplantId(handshakeRequest, httpContext);

        // 3. The session is now open and the stream is the tasking channel. Hold
        //    it open, draining results and pushing queued tasks; close the session
        //    when the connection ends -- whether the implant closed cleanly or the
        //    stream was aborted.
        try
        {
            await RunSessionAsync(implant, requestStream, responseStream, context.CancellationToken);
        }
        finally
        {
            await _sessions.CloseAsync(session.Value, DateTimeOffset.UtcNow, CancellationToken.None);
        }
    }

    // The tasking session (roadmap M1.4): a reader draining result frames and a
    // writer pushing queued tasks downstream, run concurrently. Concurrency is
    // required because tasks enter the queue out-of-band -- an operator POSTs
    // them over HTTP, not over this stream -- so the writer must keep polling the
    // queue even while the reader is blocked awaiting the next result. A strictly
    // sequential read-then-dispatch would deadlock: the reader blocks on a result
    // the implant never sends because the task that prompts it is still queued.
    //
    // gRPC allows only one outstanding write per stream; the writer is the sole
    // caller of WriteAsync here, so there is no contention. Either loop ending
    // (clean client close in the reader, cancellation) ends the session; the
    // offline finally above runs regardless.
    private async Task RunSessionAsync(
        ImplantId implant,
        IAsyncStreamReader<Frame> requestStream,
        IServerStreamWriter<Frame> responseStream,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = ReadResultsAsync(requestStream, linked.Token);
        var writer = DispatchTasksAsync(implant, responseStream, linked.Token);

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
            // Expected: the loop we cancelled unwinds through Task.Delay.
        }
    }

    // Reader: await each result frame, capture it into the task and append the
    // audit event. Ends on a clean client close (MoveNext returns false); throws
    // on an abort. Non-result frames are ignored for now (keepalives, etc.).
    private async Task ReadResultsAsync(
        IAsyncStreamReader<Frame> requestStream,
        CancellationToken cancellationToken)
    {
        while (await requestStream.MoveNext(cancellationToken))
            await HandleFrameAsync(requestStream.Current, cancellationToken);
    }

    // Writer: poll the queue and push each queued task downstream. Operators task
    // implants over HTTP at any moment, so this loops for the life of the session
    // rather than draining once. The short delay keeps it from busy-waiting when
    // the queue is empty; a real scheduler drives this off a channel later.
    private async Task DispatchTasksAsync(
        ImplantId implant,
        IServerStreamWriter<Frame> responseStream,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await DispatchNextAsync(implant, responseStream, cancellationToken);
            await Task.Delay(DispatchPollInterval, cancellationToken);
        }
    }

    private static readonly TimeSpan DispatchPollInterval = TimeSpan.FromMilliseconds(25);


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
        var frame = new Frame { Payload = ByteString.CopyFrom(request.ToByteArray()) };
        await responseStream.WriteAsync(frame);
    }

    // A result frame: capture the outcome into the task and append the audit
    // event. This is the transport-layer composition the M1.4 AC calls for --
    // task state lives in core, the audit event in the audit layer, and the
    // beacon stream is where both meet on a completed task (architecture.md
    // Sec 10.3/11).
    private async Task HandleFrameAsync(Frame frame, CancellationToken cancellationToken)
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

        var completed = await _tasks.RecordResultAsync(taskId, result.Output, outcome, cancellationToken);

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

        // Fan the completion out to connected operator sessions (roadmap M2.4).
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

    private async Task<(HandshakeResponse Response, SessionId? Session)> TryHandshakeAsync(
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

            return (Response(HandshakeStatus.Ok, result.EngagementId.ToString()), result.SessionId);
        }
        catch (HandshakeException ex)
        {
            var status = ex.Reason switch
            {
                HandshakeReason.UnknownImplant => HandshakeStatus.UnknownImplant,
                HandshakeReason.VersionMismatch => HandshakeStatus.VersionMismatch,
                HandshakeReason.IdentityMismatch => HandshakeStatus.IdentityMismatch,
                _ => HandshakeStatus.Unspecified,
            };
            return (Response(status, engagementId: null), Session: null);
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
}
