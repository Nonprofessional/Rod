using Google.Protobuf;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Implants;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;
using Rod.Transport.Endpoints;
using Rod.V1;
using Task = System.Threading.Tasks.Task;

namespace Rod.Transport.Listeners.Streams;

// The stream check-in bridge (architecture.md Sec 8): the transport-blind
// check-in flow the named-pipe and raw-TCP listeners share. One connection is
// one poll check-in -- the request message carries the handshake first, then
// any results, exfil chunks, staged pulls, and channel output; the response
// message carries the handshake response, staged chunk runs answering the
// request's demands, and queued tasking while the dispatch budget lasts. The
// per-frame paths are the shared beacon compositions (BeaconIngest,
// BeaconTasking), so a result captured over a stream listener is
// indistinguishable in core state, the audit trail, and the live bus from one
// captured over the gRPC stream -- the same property the envelope carries.
//
// The identity posture is the certificate-less one (Sec 8): no client
// certificate rides a pipe or a raw socket, so the implant is identified by
// the id in its handshake -- the DNS posture extended to a handshake-capable
// transport. The enrolled, kill-date, and retired gates apply in full, and
// dispatched tasking keeps the full Sec 9 signature posture: an implant that
// verifies its tasking (Tier 1) is protected no matter which transport
// delivered it.

/// <summary>
/// Serves one check-in over a duplex stream: read the request message, run
/// the envelope's sequential poll flow (handshake, ingest, staged answers,
/// budgeted dispatch), and write the response message.
/// </summary>
internal sealed class StreamBeaconBridge
{
    /// <summary>
    /// The dispatched-tasking budget for one response message, the same budget
    /// the envelope applies: tasking frames are claimed only while they fit,
    /// and a task that does not fit is requeued for the next check-in.
    /// </summary>
    public const int MaxDispatchBytes = 4 * 1024 * 1024;

    // How long one check-in may take end to end: a client that connects and
    // goes silent must not pin a handler on a transport with no HTTP timeouts
    // of its own. Generous against a slow poll cycle, bounded against a dead
    // peer.
    private static readonly TimeSpan CheckInTimeout = TimeSpan.FromSeconds(30);

    private readonly HandshakeService _handshake;
    private readonly ISessionRegistry _sessions;
    private readonly TaskService _tasks;
    private readonly BeaconIngest _ingest;
    private readonly BeaconTasking _tasking;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _clock;

    public StreamBeaconBridge(
        HandshakeService handshake,
        ISessionRegistry sessions,
        TaskService tasks,
        BeaconIngest ingest,
        BeaconTasking tasking,
        IAuditStore audit,
        TimeProvider clock)
    {
        _handshake = handshake;
        _sessions = sessions;
        _tasks = tasks;
        _ingest = ingest;
        _tasking = tasking;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// Handles one connection as one check-in and closes it. Every failure --
    /// a malformed message, a vanished client, a refused handshake answered
    /// with a bare handshake response -- ends the connection; the next
    /// check-in reconnects, the poll cadence implants already keep.
    /// </summary>
    public async Task HandleCheckInAsync(Stream stream, CancellationToken stoppingToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        bounded.CancelAfter(CheckInTimeout);
        var cancellationToken = bounded.Token;
        try
        {
            byte[] body;
            List<Frame> frames;
            try
            {
                body = await StreamCheckInFraming.ReadMessageAsync(stream, cancellationToken);
                frames = EnvelopeFraming.Parse(body);
            }
            catch (Exception ex) when (ex is EnvelopeFramingException or IOException)
            {
                // A malformed or oversized check-in gets no answer: the
                // connection is dropped, not negotiated.
                return;
            }

            // The implant speaks first here too: the first frame is the
            // handshake, and -- with no certificate to read an identity from --
            // the handshake is the identity: the implant id it carries.
            HandshakeRequest handshakeRequest;
            if (frames.Count == 0
                || !TryParseHandshake(frames[0], out handshakeRequest)
                || !ImplantId.TryParse(handshakeRequest.ImplantId, out var implantId))
            {
                await RespondAsync(stream, Response(HandshakeStatus.Unspecified, engagementId: null, replayNonces: false), stoppingToken);
                return;
            }

            var (response, handshake) = await TryHandshakeAsync(implantId, handshakeRequest);
            if (response.Status != HandshakeStatus.Ok || handshake is null)
            {
                await RespondAsync(stream, response, stoppingToken);
                return;
            }

            // A genuinely new session is recorded; a reused one (every
            // check-in after the first) is not, the same flood guard the
            // stream and the envelope apply (architecture.md Sec 10.3, Sec 11).
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

            var session = new BeaconSessionContext(
                implantId,
                handshake.EngagementId,
                handshake.SessionId,
                handshake.DeployedBy,
                handshakeRequest.Capabilities);

            // One presence touch per check-in, then the session guard: if the
            // session this handshake holds was closed out from under it, stop
            // after the handshake response so the implant re-handshakes on its
            // next cycle.
            await _sessions.TouchAsync(session.Implant, session.Capabilities, _clock.GetUtcNow(), cancellationToken);
            var active = await _sessions.GetActiveAsync(session.Implant, cancellationToken);
            if (active is null || active.Id != session.SessionId)
            {
                await RespondAsync(stream, response, stoppingToken);
                return;
            }

            var outbound = new List<Frame> { HandshakeFrame(response) };

            // Ingest the request's remaining frames (results, exfil chunks,
            // staged pulls, channel output) through the shared composition,
            // collecting validated staged demands for the response.
            var connection = _ingest.OpenConnection();
            var stagedPulls = new List<TaskId>();
            for (var i = 1; i < frames.Count; i++)
            {
                await connection.IngestAsync(
                    session,
                    frames[i],
                    stagedPullSink: stagedPulls.Add,
                    cancellationToken);
            }

            // Answer each demand with its chunk run in this response, before
            // any new tasking: the implant is blocked on bytes it already
            // accepted a task for (architecture.md Sec 10, the typed arm).
            foreach (var pull in stagedPulls)
                outbound.AddRange(await _tasking.StagedChunkRunAsync(pull.Value, cancellationToken));

            // Dispatch queued tasking while the budget lasts. A channel task
            // is requeued untouched and ends the drain: its channel needs a
            // live stream for the input half (architecture.md Sec 10.3), the
            // same rule the envelope and DNS transports apply, so it parks at
            // the queue head for a stream transport to claim.
            var budget = MaxDispatchBytes;
            while (true)
            {
                var dispatched = await _tasks.DispatchNextAsync(session.Implant, cancellationToken);
                if (dispatched is null)
                    break;

                var frame = _tasking.MarshalFrame(dispatched);
                var wireSize = EnvelopeFraming.WireSize(frame);

                if (ChannelVerbs.IsChannelVerb(dispatched.Verb) || wireSize > budget)
                {
                    await _tasks.RequeueAsync(dispatched.TaskId, CancellationToken.None);
                    break;
                }

                budget -= wireSize;
                outbound.Add(frame);
                await _tasking.RecordDispatchAsync(dispatched, cancellationToken);
            }

            await StreamCheckInFraming.WriteMessageAsync(
                stream, EnvelopeFraming.Encode(outbound), stoppingToken);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException
            or IOException
            or ObjectDisposedException
            or System.Net.Sockets.SocketException)
        {
            // The client vanished or the host is stopping: the connection
            // ends, and the next check-in reconnects. A dispatched task whose
            // response write failed stays claimed for its result -- the same
            // retransmission tolerance the envelope carries.
        }
    }

    private async Task RespondAsync(Stream stream, HandshakeResponse response, CancellationToken stoppingToken)
        => await StreamCheckInFraming.WriteMessageAsync(
            stream, EnvelopeFraming.Encode(new[] { HandshakeFrame(response) }), stoppingToken);

    private static bool TryParseHandshake(Frame frame, out HandshakeRequest request)
    {
        try
        {
            request = HandshakeRequest.Parser.ParseFrom(frame.Payload);
            return true;
        }
        catch (InvalidProtocolBufferException)
        {
            request = new HandshakeRequest();
            return false;
        }
    }

    private async Task<(HandshakeResponse Response, HandshakeResult? Handshake)> TryHandshakeAsync(
        ImplantId implantId,
        HandshakeRequest request)
    {
        try
        {
            var result = await _handshake.HandshakeAsync(
                new HandshakeCommand(
                    ImplantId: implantId,
                    MajorVersion: request.Version?.Major ?? -1,
                    MinorVersion: request.Version?.Minor ?? -1,
                    Capabilities: request.Capabilities,
                    // No certificate rides this transport (Sec 8): the null
                    // binding is the id-alone posture, and the enrolled,
                    // kill-date, and retired gates still apply.
                    CertificateEngagementId: null,
                    ReplayNonces: request.ReplayNonces),
                CancellationToken.None);
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

    private static HandshakeResponse Response(HandshakeStatus status, string? engagementId, bool replayNonces)
        => new()
        {
            Status = status,
            Version = new ProtocolVersion { Major = ProtocolVersions.Major, Minor = ProtocolVersions.Minor },
            EngagementId = engagementId ?? string.Empty,
            ReplayNonces = replayNonces,
        };

    private static Frame HandshakeFrame(HandshakeResponse response)
        => new() { Payload = ByteString.CopyFrom(response.ToByteArray()) };
}
