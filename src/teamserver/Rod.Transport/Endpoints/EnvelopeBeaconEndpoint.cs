using System.Buffers.Binary;
using System.Text;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;
using Rod.CoreState.Transports;
using Rod.Transport.Payloads;
using Rod.V1;
using Task = System.Threading.Tasks.Task;

namespace Rod.Transport.Endpoints;

// The plain-HTTP envelope check-in (architecture.md Sec 8, the implant-reach
// transport): the same rod.v1 Frames the gRPC stream carries, as
// varint-length-delimited sequences in ordinary HTTP request/response bodies.
// One POST is one poll check-in -- the request body carries the handshake
// first, then any results, exfil chunks, staged pulls, and channel output;
// the response body carries the handshake response, then staged chunk runs
// answering the request's demands, then queued tasking while the dispatch
// budget lasts. Dropping the gRPC/HTTP-2 requirement is the point: Tier 0 is
// reachable from any language with an HTTP client and a protobuf codec
// (extending/implants.md).
//
// Authentication is at the application layer, under the per-artifact key the
// build minted (the mainstream HTTP(S) C2 shape -- no TLS certificate request
// anywhere): the default body is the R1 envelope sealing
// counter || framed-frames under that key, and the response seals the same
// way, so the cleartext-http posture carries confidential content, not just
// authenticated content. The plaintext framed body is the lab-debug toggle's
// shape, refused for implants whose enrollment bound them to a key.

/// <summary>
/// Maps the envelope check-in route. Mapped alongside the operator API on
/// every listener like the gRPC beacon. The identity is the artifact key that
/// sealed the body; a client certificate, where the mTLS front presented one,
/// still resolves first; and the handshake's implant id alone -- the
/// anything-with-reach posture of the cleartext lab shape -- serves only an
/// implant no key was ever bound to.
/// </summary>
public static class EnvelopeBeaconEndpoints
{
    /// <summary>The envelope check-in route, in the implant family with enroll.</summary>
    public const string Route = "/implants/beacon";

    public static IEndpointRouteBuilder MapEnvelopeBeaconEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, async (
            HttpContext http,
            EnvelopeBeaconCheckIn checkIn,
            CancellationToken cancellationToken)
            => await checkIn.HandleAsync(http, cancellationToken))
            .WithName(nameof(EnvelopeBeaconCheckIn));
        return endpoints;
    }
}

/// <summary>
/// One envelope check-in. The per-frame paths are the shared beacon
/// compositions (<see cref="BeaconIngest"/>, <see cref="BeaconTasking"/>), so
/// a result captured over the envelope is indistinguishable in core state,
/// the audit trail, and the live bus from one captured over the stream. The
/// poll shape is sequential -- ingest the request's frames, then dispatch
/// queued tasking into the response -- with no push loops: a POST is one
/// check-in cycle, and the implant sleeps the baked interval between them.
/// </summary>
internal sealed class EnvelopeBeaconCheckIn
{
    /// <summary>
    /// The dispatched-tasking budget for one response body: tasking frames are
    /// claimed only while they fit, and a task that does not fit is requeued
    /// for the next check-in -- it never strands in Dispatched
    /// (architecture.md Sec 10.3). Staged chunk runs are exempt: a demand is
    /// answered whole, because its size was fixed by the operator's staged
    /// upload, and an implant waits on the terminal chunk.
    /// </summary>
    public const int MaxDispatchBytes = 4 * 1024 * 1024;

    private readonly HandshakeService _handshake;
    private readonly ISessionRegistry _sessions;
    private readonly TaskService _tasks;
    private readonly BeaconIngest _ingest;
    private readonly BeaconTasking _tasking;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _clock;
    private readonly IPayloadStore _payloads;
    private readonly EnvelopeCheckInKeys _checkInKeys;
    private readonly Rod.Transport.Channels.DegradedChannelHub _degraded;

    public EnvelopeBeaconCheckIn(
        HandshakeService handshake,
        ISessionRegistry sessions,
        TaskService tasks,
        BeaconIngest ingest,
        BeaconTasking tasking,
        IAuditStore audit,
        TimeProvider clock,
        IPayloadStore payloads,
        EnvelopeCheckInKeys checkInKeys,
        Rod.Transport.Channels.DegradedChannelHub degraded)
    {
        _handshake = handshake;
        _sessions = sessions;
        _tasks = tasks;
        _ingest = ingest;
        _tasking = tasking;
        _audit = audit;
        _clock = clock;
        _payloads = payloads;
        _checkInKeys = checkInKeys;
        _degraded = degraded;
    }

    public async Task<IResult> HandleAsync(HttpContext http, CancellationToken cancellationToken)
    {
        // The identity the transport itself presented, when it presented one:
        // a client certificate on an mTLS front. The https and http listeners
        // never ask for a certificate (a TLS CertificateRequest is itself a
        // fingerprint), so most check-ins carry none -- the sealed body below
        // is what authenticates those.
        var identity = ClientCertificateIdentity.Read(http);

        byte[] body;
        try
        {
            body = await EnvelopeFraming.ReadBodyAsync(http.Request.Body, cancellationToken);
        }
        catch (EnvelopeFramingException ex) when (ex.Oversized)
        {
            return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);
        }

        // The sealed body (architecture.md Sec 8/9): base64 of
        // magic || keyId || nonce || ciphertext || tag, AES-256-GCM under the
        // artifact's per-build key, wrapping a strictly increasing counter
        // (8 bytes, big-endian) ahead of the framed bytes. The key id resolves
        // the key beside the stored payload; the GCM tag authenticates the
        // whole body, counter and frames together. The wire-body cap above
        // governs the base64 text, so the frames inside ride a ~3/4 share of
        // it -- the budget was sized for exfil runs, not for this overhead.
        long checkInCounter = 0;
        var sealedKey = (KeyId: Guid.Empty, Key: Array.Empty<byte>());
        var isSealed = false;
        var framed = body;
        if (TryReadSealedKeyId(body, out var sealedText) is { } keyId)
        {
            var carrier = await _payloads.FindByEnvelopeKeyAsync(keyId, cancellationToken);
            if (carrier?.EnvelopeKey is not { } key
                || AesGcmEnvelope.TryUnwrap(sealedText, keyId, key, AesGcmEnvelope.CheckInRequestAad)
                    is not { } plaintext
                || plaintext.Length < CounterBytes)
                return Results.Json(
                    new Problem("The check-in body did not verify under its artifact key."),
                    statusCode: StatusCodes.Status401Unauthorized);

            checkInCounter = BinaryPrimitives.ReadInt64BigEndian(plaintext);
            framed = plaintext[CounterBytes..];
            sealedKey = (keyId, key);
            isSealed = true;
        }

        // Every envelope response after a verified unwrap seals the same way,
        // handshake refusals included: the sealed client must be able to read
        // its own refusal statuses, and an https or cleartext front leaks no
        // frame bytes in either direction.
        IResult Reply(IReadOnlyList<Frame> outbound)
            => isSealed
                ? Results.Text(
                    AesGcmEnvelope.Wrap(
                        EnvelopeFraming.Encode(outbound), sealedKey.KeyId, sealedKey.Key,
                        AesGcmEnvelope.CheckInResponseAad),
                    "text/plain",
                    Encoding.UTF8)
                : Results.Bytes(EnvelopeFraming.Encode(outbound), "application/octet-stream");

        List<Frame> frames;
        try
        {
            frames = EnvelopeFraming.Parse(framed);
        }
        catch (EnvelopeFramingException ex)
        {
            return ex.Oversized
                ? Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge)
                : Results.BadRequest(new Problem("The request body is not a delimited frame sequence."));
        }

        // The implant speaks first here too: the first frame is the handshake.
        HandshakeRequest handshakeRequest;
        if (frames.Count == 0 || !TryParseHandshake(frames[0], out handshakeRequest))
            return Reply(new[] { HandshakeFrame(Response(HandshakeStatus.Unspecified, engagementId: null, replayNonces: false)) });

        // The key posture gates, checked before the handshake opens anything:
        // an implant bound to a build key at enroll checks in sealed under
        // exactly that key (a plaintext body from it is the refused downgrade;
        // another artifact's key does not impersonate it), and the counter
        // must clear the floor -- a replayed body, whatever it claims, turns
        // away without a session, a touch, or an audit record.
        if (ImplantId.TryParse(handshakeRequest.ImplantId, out var sealedImplant))
        {
            if (_checkInKeys.TryGet(sealedImplant) is { } bound)
            {
                if (!isSealed)
                    return Results.Json(
                        new Problem("This implant checks in under its artifact key; the plaintext frame body is refused."),
                        statusCode: StatusCodes.Status401Unauthorized);
                if (sealedKey.KeyId != bound.KeyId)
                    return Results.Json(
                        new Problem("The check-in body did not verify under its artifact key."),
                        statusCode: StatusCodes.Status401Unauthorized);
            }
            if (isSealed && !_checkInKeys.Accept(sealedImplant, checkInCounter))
                return Results.Json(
                    new Problem("The check-in body carries a counter at or below the accepted floor."),
                    statusCode: StatusCodes.Status401Unauthorized);
        }

        // The identity for the handshake: the certificate binding when the
        // transport presented one, else the handshake's implant id -- which
        // the sealed body authenticated above, and which stands by reach only
        // in the cleartext lab posture (the anything-with-reach tradeoff the
        // cleartext gRPC stream and the DNS/SMB/TCP transports document). Over
        // TLS with neither, there is no identity to offer and the handshake
        // refuses the unknown implant.
        var (response, handshake) = await TryHandshakeAsync(
            _handshake, identity, handshakeRequest, isSealed || !http.Request.IsHttps);
        if (response.Status != HandshakeStatus.Ok || handshake is null)
            return Reply(new[] { HandshakeFrame(response) });

        // A genuinely new session is recorded; a reused one (every check-in
        // after the first) is not, the same flood guard the stream applies
        // (architecture.md Sec 10.3, Sec 11).
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

        // The session context carries the handshake's identity (the implant
        // the handshake authenticated), not the certificate binding: over
        // cleartext there is no certificate, and the handshake result already
        // resolved one identity or refused.
        var session = new BeaconSessionContext(
            handshake.ImplantId,
            handshake.EngagementId,
            handshake.SessionId,
            handshake.DeployedBy,
            handshakeRequest.Capabilities);

        // One presence touch per check-in -- a POST is the poll-cadence unit
        // here, not the individual frame. Then the stream's session guard: if
        // the session this handshake holds was closed out from under it, stop
        // after the handshake response so the implant re-handshakes on its
        // next cycle.
        await _sessions.TouchAsync(session.Implant, session.Capabilities, _clock.GetUtcNow(), cancellationToken);
        var active = await _sessions.GetActiveAsync(session.Implant, cancellationToken);
        if (active is null || active.Id != session.SessionId)
            return Reply(new[] { HandshakeFrame(response) });

        var outbound = new List<Frame> { HandshakeFrame(response) };

        // Ingest the request's remaining frames (results, exfil chunks, staged
        // pulls, channel output) through the shared composition, collecting
        // validated staged demands for the response.
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

        // Answer each demand with its chunk run in this response, before any
        // new tasking: the implant is blocked on bytes it already accepted a
        // task for (architecture.md Sec 10, the typed arm).
        foreach (var pull in stagedPulls)
            outbound.AddRange(await _tasking.StagedChunkRunAsync(pull.Value, cancellationToken));

        // Dispatch queued tasking while the budget lasts. The claim
        // evaluation every poll carrier runs decides what fits: a channel
        // verb claims only under the degraded discipline -- the session's
        // opt-in -- and without it parks at the queue head for a stream
        // transport to claim (architecture.md Sec 10.3).
        var degraded = session.Capabilities.Contains(Channels.DegradedChannelHub.Capability);
        var budget = MaxDispatchBytes;
        while (true)
        {
            var dispatched = await _tasks.DispatchNextAsync(session.Implant, cancellationToken);
            if (dispatched is null)
                break;

            var frame = _tasking.MarshalFrame(dispatched);
            var wireSize = EnvelopeFraming.WireSize(frame);

            if (TransportCapabilities.EvaluateClaim(
                    TransportCapabilities.Envelope, dispatched.Verb, wireSize, budget, degraded)
                != ClaimDecision.Claim)
            {
                await _tasks.RequeueAsync(dispatched.TaskId, CancellationToken.None);
                break;
            }

            budget -= wireSize;
            outbound.Add(frame);
            await _tasking.RecordDispatchAsync(dispatched, cancellationToken);
        }

        // The store-and-forward half: parked operator input rides after the
        // tasking, as ChannelInput frames within the budget the tasking left
        // -- the poll cycle's answer to the live stream's input pump. The
        // sweep on the way closes channels the implant stopped collecting.
        if (degraded)
        {
            await _degraded.SweepIdleAsync(cancellationToken);
            outbound.AddRange(_degraded.Drain(session.Implant, budget));
        }

        return Reply(outbound);
    }

    /// <summary>
    /// The sealed check-in counter's size in bytes: an 8-byte unsigned
    /// big-endian integer, room for one fresh value per attempt for any
    /// cadence an implant will ever run.
    /// </summary>
    private const int CounterBytes = 8;

    /// <summary>
    /// Reads the R1 key id off a request body, or null when the body is not
    /// the sealed shape -- the plaintext framed body. A framed body whose
    /// bytes happen to be base64 still decodes to protobuf, not the
    /// magic-prefixed envelope, and a sealed body names a key id only the
    /// build could have baked.
    /// </summary>
    internal static Guid? TryReadSealedKeyId(byte[] body, out string sealedText)
    {
        sealedText = Encoding.UTF8.GetString(body).Trim();
        return AesGcmEnvelope.TryReadKeyId(sealedText);
    }

    internal static bool TryParseHandshake(Frame frame, out HandshakeRequest request)
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

    // Shared with the WebSocket stream's handshake (the same service call and
    // status mapping over a different transport); static so both endpoints
    // reach it without sharing state.
    internal static async Task<(HandshakeResponse Response, HandshakeResult? Handshake)> TryHandshakeAsync(
        HandshakeService handshake,
        ClientIdentity? identity,
        HandshakeRequest request,
        bool cleartextFallback)
    {
        try
        {
            var result = await handshake.HandshakeAsync(
                new HandshakeCommand(
                    ImplantId: identity?.ImplantId
                        ?? (cleartextFallback && ImplantId.TryParse(request.ImplantId, out var byId) ? byId : default),
                    MajorVersion: request.Version?.Major ?? -1,
                    MinorVersion: request.Version?.Minor ?? -1,
                    Capabilities: request.Capabilities,
                    CertificateEngagementId: identity?.EngagementId,
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

    internal static HandshakeResponse Response(HandshakeStatus status, string? engagementId, bool replayNonces)
        => new()
        {
            Status = status,
            Version = new ProtocolVersion { Major = ProtocolVersions.Major, Minor = ProtocolVersions.Minor },
            EngagementId = engagementId ?? string.Empty,
            ReplayNonces = replayNonces,
        };

    internal static Frame HandshakeFrame(HandshakeResponse response)
        => new() { Payload = ByteString.CopyFrom(response.ToByteArray()) };

    public sealed record Problem(string Error);
}
