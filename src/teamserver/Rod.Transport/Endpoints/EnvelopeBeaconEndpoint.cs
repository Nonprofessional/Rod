using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;
using Rod.V1;
using Task = System.Threading.Tasks.Task;

namespace Rod.Transport.Endpoints;

// The plain-HTTP envelope check-in (architecture.md Sec 8, the implant-reach
// escape hatch): the same rod.v1 Frames the gRPC stream carries, as
// varint-length-delimited sequences in ordinary HTTPS request/response bodies
// over the same client certificates. One POST is one poll check-in -- the
// request body carries the handshake first, then any results, exfil chunks,
// staged pulls, and channel output; the response body carries the handshake
// response, then staged chunk runs answering the request's demands, then
// queued tasking while the dispatch budget lasts. Dropping the gRPC/HTTP-2
// requirement is the point: Tier 0 is reachable from any language with an
// HTTP client and a protobuf codec (extending/implants.md).

/// <summary>
/// Maps the envelope check-in route. Mapped alongside the operator API on
/// every listener like the gRPC beacon: the route itself demands the
/// mTLS-presented implant certificate, so on a plain-HTTP listener it answers
/// 401 and only an mTLS-terminated endpoint ever serves a check-in.
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

    public EnvelopeBeaconCheckIn(
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

    public async Task<IResult> HandleAsync(HttpContext http, CancellationToken cancellationToken)
    {
        // The envelope rides the same client certificates as the stream
        // (architecture.md Sec 8): the identity is the certificate binding,
        // full stop. Without one -- the route reached over a listener that did
        // not terminate mTLS -- there is no check-in to serve.
        var identity = ClientCertificateIdentity.Read(http);
        if (identity is null)
            return Results.Json(
                new Problem("A client certificate bound to an implant is required."),
                statusCode: StatusCodes.Status401Unauthorized);

        byte[] body;
        try
        {
            body = await EnvelopeFraming.ReadBodyAsync(http.Request.Body, cancellationToken);
        }
        catch (EnvelopeFramingException ex) when (ex.Oversized)
        {
            return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);
        }

        List<Frame> frames;
        try
        {
            frames = EnvelopeFraming.Parse(body);
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
            return EnvelopeResponse(Response(HandshakeStatus.Unspecified, engagementId: null, replayNonces: false));

        var (response, handshake) = await TryHandshakeAsync(identity, handshakeRequest);
        if (response.Status != HandshakeStatus.Ok || handshake is null)
            return EnvelopeResponse(response);

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

        var session = new BeaconSessionContext(
            identity.ImplantId,
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
            return EnvelopeResponse(response);

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

        // Dispatch queued tasking while the budget lasts. A channel task is
        // requeued untouched and ends the drain: its channel needs a live
        // stream for the input half (architecture.md Sec 10.3), the same rule
        // the DNS transport applies, so it parks at the queue head for a
        // stream transport to claim.
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

        return Results.Bytes(EnvelopeFraming.Encode(outbound), "application/octet-stream");
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
        ClientIdentity identity,
        HandshakeRequest request)
    {
        try
        {
            var result = await _handshake.HandshakeAsync(
                new HandshakeCommand(
                    ImplantId: identity.ImplantId,
                    MajorVersion: request.Version?.Major ?? -1,
                    MinorVersion: request.Version?.Minor ?? -1,
                    Capabilities: request.Capabilities,
                    CertificateEngagementId: identity.EngagementId,
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

    private static IResult EnvelopeResponse(HandshakeResponse response)
        => Results.Bytes(EnvelopeFraming.Encode(new[] { HandshakeFrame(response) }), "application/octet-stream");

    public sealed record Problem(string Error);
}
