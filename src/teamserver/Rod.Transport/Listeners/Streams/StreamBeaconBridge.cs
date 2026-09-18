using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Implants;
using Rod.CoreState.Sessions;
using Rod.CoreState.Staging;
using Rod.CoreState.Tasks;
using Rod.CoreState.Transports;
using Rod.Transport.Channels;
using Rod.Transport.Endpoints;
using Rod.Transport.Payloads;
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
// The opening message may also carry an EnrollRequest ahead of its handshake
// (Sec 8, the same full-independence step QUIC took): the certificate-less
// posture makes the carriage clean -- no TLS, no second connection -- so a
// no-egress segment can enroll its first implant over the pipe or socket it
// already reaches.
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
    private readonly DegradedChannelHub _degraded;
    private readonly EnrollmentService _enrollment;
    private readonly IStagerTokenService _tokens;
    private readonly IPayloadStore _payloads;
    private readonly EnvelopeCheckInKeys _checkInKeys;
    private readonly ILogger<StreamBeaconBridge> _logger;

    public StreamBeaconBridge(
        HandshakeService handshake,
        ISessionRegistry sessions,
        TaskService tasks,
        BeaconIngest ingest,
        BeaconTasking tasking,
        IAuditStore audit,
        TimeProvider clock,
        DegradedChannelHub degraded,
        EnrollmentService enrollment,
        IStagerTokenService tokens,
        IPayloadStore payloads,
        EnvelopeCheckInKeys checkInKeys,
        ILogger<StreamBeaconBridge> logger)
    {
        _handshake = handshake;
        _sessions = sessions;
        _tasks = tasks;
        _ingest = ingest;
        _tasking = tasking;
        _audit = audit;
        _clock = clock;
        _degraded = degraded;
        _enrollment = enrollment;
        _tokens = tokens;
        _payloads = payloads;
        _checkInKeys = checkInKeys;
        _logger = logger;
    }

    /// <summary>
    /// Handles one connection as one check-in and closes it. Every failure --
    /// a malformed message, a vanished client, a refused handshake answered
    /// with a bare handshake response -- ends the connection; the next
    /// check-in reconnects, the poll cadence implants already keep.
    /// </summary>
    public async Task HandleCheckInAsync(Stream stream, Listener listener, CancellationToken stoppingToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        bounded.CancelAfter(CheckInTimeout);
        var cancellationToken = bounded.Token;
        try
        {
            // The opening message may carry an EnrollRequest ahead of its
            // handshake (Sec 8), so the first decode accepts the enroll
            // seal too; every later message on the connection is a check-in.
            var opened = await ReadAndDecodeAsync(stream, allowEnroll: true, cancellationToken);
            if (opened is null)
                return;
            var frames = opened.Frames;
            var sealedKey = (KeyId: opened.Sealed ? opened.KeyId : Guid.Empty, opened.Key);
            var isSealed = opened.Sealed;
            var checkInCounter = opened.Counter;

            // Enrollment over the stream check-in (Sec 8, the same
            // full-independence step QUIC took): the opening message may
            // carry an EnrollRequest ahead of its handshake -- the
            // certificate-less posture makes the carriage clean, no TLS and
            // no second connection. The arm answers as its own message and
            // reads the next message as the check-in.
            if (frames.Count > 0 && frames[0].Kind == FrameKind.EnrollRequest)
            {
                if (!await ServeEnrollmentAsync(stream, listener, frames[0], opened, cancellationToken))
                    return;

                // The handshake is the next frame the connection carries: the
                // remainder of the enroll message when it rode one, else the
                // next message's first frame.
                frames = frames.Skip(1).ToList();
                if (frames.Count == 0)
                {
                    var next = await ReadAndDecodeAsync(stream, allowEnroll: false, cancellationToken);
                    if (next is null)
                        return;
                    frames = next.Frames;
                    sealedKey = (next.Sealed ? next.KeyId : Guid.Empty, next.Key);
                    isSealed = next.Sealed;
                    checkInCounter = next.Counter;
                }
            }

            // The implant speaks first here too: the first frame is the
            // handshake, and -- with no certificate to read an identity from --
            // the handshake is the identity: the implant id it carries.
            HandshakeRequest handshakeRequest;
            if (frames.Count == 0
                || !TryParseHandshake(frames[0], out handshakeRequest)
                || !ImplantId.TryParse(handshakeRequest.ImplantId, out var implantId))
            {
                await RespondAsync(stream, BeaconHandshake.Response(HandshakeStatus.Unspecified, engagementId: null, replayNonces: false), sealedKey, isSealed, stoppingToken);
                return;
            }

            // The key posture gates, checked before the handshake opens
            // anything (the envelope route's own rules, carried here): an
            // implant bound to a build key at enroll checks in sealed under
            // exactly that key -- a plaintext body from it is the refused
            // downgrade, another artifact's key does not impersonate it --
            // and the counter must clear the floor: a replayed body,
            // whatever it claims, turns away without a session or a touch.
            // The refusal is the dropped connection, the raw carriage's
            // answer to the HTTP problem body.
            if (_checkInKeys.TryGet(implantId) is { } bound)
            {
                if (!isSealed || sealedKey.KeyId != bound.KeyId)
                    return;
            }
            if (isSealed && !_checkInKeys.Accept(implantId, checkInCounter))
                return;

            var (response, handshake) = await TryHandshakeAsync(implantId, handshakeRequest);
            if (response.Status != HandshakeStatus.Ok || handshake is null)
            {
                await RespondAsync(stream, response, sealedKey, isSealed, stoppingToken);
                return;
            }

            // A genuinely new session is recorded; a reused one (every
            // check-in after the first) is not, the same flood guard the
            // stream and the envelope apply (architecture.md Sec 10.3, Sec 11).
            await BeaconHandshake.AppendSessionOpenedAsync(_audit, handshake, handshakeRequest);

            var session = new BeaconSessionContext(
                implantId,
                handshake.EngagementId,
                handshake.SessionId,
                handshake.DeployedBy,
                handshakeRequest.Capabilities,
                handshake.TaskAcks);

            // One presence touch per check-in, then the session guard: if the
            // session this handshake holds was closed out from under it, stop
            // after the handshake response so the implant re-handshakes on its
            // next cycle.
            await _sessions.TouchAsync(session.Implant, session.Capabilities, _clock.GetUtcNow(), "pipe", cancellationToken);
            var active = await _sessions.GetActiveAsync(session.Implant, cancellationToken);
            if (active is null || active.Id != session.SessionId)
            {
                await RespondAsync(stream, response, sealedKey, isSealed, stoppingToken);
                return;
            }

            var outbound = new List<Frame> { HandshakeFrame(response) };

            // Ingest the request's remaining frames (results, exfil chunks,
            // staged pulls, channel output) through the shared composition,
            // collecting validated staged demands for the response. Receive
            // acks from a negotiated implant are accepted and handed to a
            // no-op sink: a poll carrier keeps no ack ledger -- one connection
            // is one check-in, answered whole or not at all -- so the arm's
            // requeue never applies on this path (architecture.md Sec 10.3).
            var connection = _ingest.OpenConnection();
            var stagedPulls = new List<TaskId>();
            for (var i = 1; i < frames.Count; i++)
            {
                await connection.IngestAsync(
                    session,
                    frames[i],
                    stagedPullSink: stagedPulls.Add,
                    taskAckSink: static _ => { },
                    cancellationToken);
            }

            // Answer each demand with its chunk run in this response, before
            // any new tasking: the implant is blocked on bytes it already
            // accepted a task for (architecture.md Sec 10, the typed arm).
            foreach (var pull in stagedPulls)
                outbound.AddRange(await _tasking.StagedChunkRunAsync(pull.Value, cancellationToken));

            // Dispatch queued tasking while the budget lasts. The claim
            // evaluation every poll carrier runs decides what fits: a channel
            // verb claims only under the degraded discipline -- the
            // session's opt-in -- and without it parks at the queue head for
            // a stream transport to claim (architecture.md Sec 10.3).
            var degraded = session.Capabilities.Contains(DegradedChannelHub.Capability);
            var budget = MaxDispatchBytes;
            while (true)
            {
                var dispatched = await _tasks.DispatchNextAsync(session.Implant, cancellationToken);
                if (dispatched is null)
                    break;

                var frame = _tasking.MarshalFrame(dispatched);
                var wireSize = EnvelopeFraming.WireSize(frame);

                if (TransportCapabilities.EvaluateClaim(
                        TransportCapabilities.MessagePipe, dispatched.Verb, wireSize, budget, degraded)
                    != ClaimDecision.Claim)
                {
                    await _tasks.RequeueAsync(dispatched.TaskId, CancellationToken.None);
                    break;
                }

                budget -= wireSize;
                outbound.Add(frame);
                await _tasking.RecordDispatchAsync(dispatched, cancellationToken);
            }

            // The store-and-forward half, the envelope's own: parked operator
            // input rides after the tasking as ChannelInput frames within the
            // budget the tasking left, and the sweep closes channels the
            // implant stopped collecting.
            if (degraded)
            {
                await _degraded.SweepIdleAsync(cancellationToken);
                outbound.AddRange(_degraded.Drain(session.Implant, budget));
            }

            await ReplyAsync(stream, outbound, sealedKey, isSealed, stoppingToken);
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

    // One response message out: the framed body, or the same body sealed
    // under the key the request verified with -- a sealed cycle answers
    // sealed, handshake refusals included, so the wire carries no readable
    // frame bytes in either direction.
    private static async Task ReplyAsync(
        Stream stream, IReadOnlyList<Frame> outbound, (Guid KeyId, byte[] Key) sealedKey, bool isSealed,
        CancellationToken stoppingToken)
    {
        var body = EnvelopeFraming.Encode(outbound);
        if (isSealed)
            body = System.Text.Encoding.UTF8.GetBytes(AesGcmEnvelope.Wrap(
                body, sealedKey.KeyId, sealedKey.Key, AesGcmEnvelope.CheckInResponseAad));
        await StreamCheckInFraming.WriteMessageAsync(stream, body, stoppingToken);
    }

    private async Task RespondAsync(
        Stream stream, HandshakeResponse response, (Guid, byte[]) sealedKey, bool isSealed, CancellationToken stoppingToken)
        => await ReplyAsync(
            stream, new[] { HandshakeFrame(response) }, sealedKey, isSealed, stoppingToken);

    // The enroll exchange on the opening connection (Sec 8): a kind-bearing
    // EnrollRequest frame -- the enroll body the web route carries, promoted
    // into the rod.v1 frame grammar -- answered by an EnrollResponse frame.
    // The shared ScopedEnrollment flow does the work the web route drives it
    // for, scoped by this connection's own listener (the ingress the HTTP
    // route resolves from the local port, the pipe or socket listener knows
    // directly), with the same refusal rules and audit arc. A refusal answers
    // the status frame and ends the connection (no identity exists to hold a
    // session); an acceptance is followed by the ordinary handshake on the
    // same connection.
    private async Task<bool> ServeEnrollmentAsync(
        Stream stream, Listener listener, Frame frame, DecodedMessage opened, CancellationToken cancellationToken)
    {
        Rod.V1.EnrollRequest request;
        try
        {
            request = Rod.V1.EnrollRequest.Parser.ParseFrom(frame.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            await WriteEnrollResponseAsync(
                stream, new Rod.V1.EnrollResponse { Status = EnrollStatus.Unspecified }, opened, cancellationToken);
            return false;
        }

        // A shared-tier socket refuses implant ingress outright (Sec 8) --
        // the same rule ScopedEnrollment enforces from the listener record,
        // kept here so the named refusal reads on the listener's own log.
        if (listener.EngagementId is null)
        {
            _logger.LogInformation(
                "Stream enroll refused on {Name}: the socket is not engagement-scoped.", listener.Name);
            await WriteEnrollResponseAsync(
                stream, new Rod.V1.EnrollResponse { Status = EnrollStatus.BadToken }, opened, cancellationToken);
            return false;
        }

        var outcome = await ScopedEnrollment.EnrollAsync(
            new EnrollWireFields(
                request.StagerTokenSecret,
                NullWhenEmpty(request.Class),
                request.PublicKey.IsEmpty ? null : request.PublicKey.ToByteArray(),
                NullWhenEmpty(request.ParentImplantId),
                NullWhenEmpty(request.Hostname),
                NullWhenEmpty(request.Os),
                NullWhenEmpty(request.Arch),
                NullWhenEmpty(request.Username),
                NullWhenEmpty(request.KillDate)),
            listener,
            _enrollment,
            _tokens,
            _payloads,
            _checkInKeys,
            _audit,
            _clock,
            cancellationToken);

        if (!outcome.Accepted)
        {
            // The token states carry over the wire; the problem causes (a
            // malformed request, a closed engagement) collapse to the
            // generic refusal -- the same "no signal beyond no" the web
            // route keeps -- with the cause named server-side.
            if (outcome.Problem is { } problem)
                _logger.LogInformation("Stream enroll refused on {Name}: {Problem}.", listener.Name, problem);
            else
                _logger.LogInformation(
                    "Stream enroll refused on {Name}: status {Status}.", listener.Name, outcome.Status);
            await WriteEnrollResponseAsync(
                stream, new Rod.V1.EnrollResponse { Status = outcome.Status }, opened, cancellationToken);
            return false;
        }

        var enrolled = outcome.Enrolled!;
        _logger.LogInformation(
            "Rod stream listener {Name} enrolled implant {Implant} into {Engagement}.",
            listener.Name, enrolled.ImplantId, enrolled.EngagementId);

        var response = ScopedEnrollmentResponse.Build(outcome);

        await WriteEnrollResponseAsync(stream, response, opened, cancellationToken);
        return true;
    }

    private static async Task WriteEnrollResponseAsync(
        Stream stream, Rod.V1.EnrollResponse response, DecodedMessage opened, CancellationToken cancellationToken)
    {
        var body = EnvelopeFraming.Encode(new[]
        {
            new Frame { Kind = FrameKind.EnrollResponse, Payload = ByteString.CopyFrom(response.ToByteArray()) },
        });
        // A sealed exchange answers sealed, refusals included: the client
        // that baked a key reads its answer under it, and the wire carries
        // no readable frame bytes in either direction.
        if (opened.Sealed)
            body = System.Text.Encoding.UTF8.GetBytes(AesGcmEnvelope.Wrap(
                body, opened.KeyId, opened.Key, AesGcmEnvelope.EnrollResponseAad));
        await StreamCheckInFraming.WriteMessageAsync(stream, body, cancellationToken);
    }

    private static string? NullWhenEmpty(string value)
        => value.Length == 0 ? null : value;

    // One decoded inbound message: the frames, and -- when the body was the
    // sealed shape -- the key it verified under and the counter it carried.
    private sealed record DecodedMessage(
        List<Frame> Frames, bool Sealed, Guid KeyId, byte[] Key, long Counter);

    // Reads one message and decodes its body: the plaintext framed shape, or
    // the sealed shape (architecture.md Sec 8/9) -- base64 of
    // magic || keyId || nonce || ciphertext || tag, AES-256-GCM under the
    // artifact's per-build key, the same application-layer seal the
    // cleartext http posture carries, so a bare socket or pipe leaks no
    // frame bytes either. The check-in body wraps a strictly increasing
    // counter (8 bytes, big-endian) ahead of the framed bytes; the enroll
    // exchange (the opening message, allowEnroll) seals its frames without
    // one. A body that names no known key, or does not verify under it, is
    // undecodable -- null, the dropped connection.
    private async Task<DecodedMessage?> ReadAndDecodeAsync(
        Stream stream, bool allowEnroll, CancellationToken cancellationToken)
    {
        byte[] body;
        try
        {
            body = await StreamCheckInFraming.ReadMessageAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is EnvelopeFramingException or IOException or OperationCanceledException)
        {
            return null;
        }

        var framed = body;
        var sealedShape = (Sealed: false, KeyId: Guid.Empty, Key: Array.Empty<byte>());
        long counter = 0;
        if (EnvelopeBeaconCheckIn.TryReadSealedKeyId(body, out var sealedText) is { } keyId)
        {
            var carrier = await _payloads.FindByEnvelopeKeyAsync(keyId, cancellationToken);
            if (carrier?.EnvelopeKey is not { } key)
                return null;
            byte[]? plain = null;
            if (allowEnroll)
                plain = AesGcmEnvelope.TryUnwrap(sealedText, keyId, key, AesGcmEnvelope.EnrollRequestAad);
            if (plain is null)
            {
                plain = AesGcmEnvelope.TryUnwrap(sealedText, keyId, key, AesGcmEnvelope.CheckInRequestAad);
                if (plain is null || plain.Length < 8)
                    return null;
                counter = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(plain);
                plain = plain[8..];
            }
            framed = plain;
            sealedShape = (true, keyId, key);
        }

        try
        {
            return new DecodedMessage(
                EnvelopeFraming.Parse(framed), sealedShape.Sealed, sealedShape.KeyId, sealedShape.Key, counter);
        }
        catch (EnvelopeFramingException)
        {
            // A malformed body gets no answer: the connection is dropped,
            // not negotiated.
            return null;
        }
    }

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
                    ReplayNonces: request.ReplayNonces,
                    TaskAcks: request.TaskAcks),
                CancellationToken.None);
            return (BeaconHandshake.Response(
                HandshakeStatus.Ok, result.EngagementId.ToString(),
                result.ReplayNonces, result.TaskAcks), result);
        }
        catch (HandshakeException ex)
        {
            return (BeaconHandshake.Response(
                BeaconHandshake.MapStatus(ex.Reason), engagementId: null, replayNonces: false), Handshake: null);
        }
    }

    private static Frame HandshakeFrame(HandshakeResponse response)
        => new() { Payload = ByteString.CopyFrom(response.ToByteArray()) };
}
