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
// the entity by name, so pin Task to the BCL type the method signatures need.
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
    private readonly IAuditStore _audit;
    private readonly BeaconSessionRunner _runner;

    public BeaconEndpoint(
        HandshakeService handshake,
        ISessionRegistry sessions,
        TaskService tasks,
        IAuditStore audit,
        TimeProvider clock,
        ITaskDispatchWake wake,
        LiveChannelHub channels,
        TaskRelayHub relays,
        SocksProxyHub socks,
        BeaconIngest ingest,
        BeaconTasking tasking)
    {
        _handshake = handshake;
        _audit = audit;
        // The transport-agnostic session loop this endpoint hands its adapted
        // gRPC reader and writer to: everything between the handshake and the
        // stream's end is the frame paths' business, not the transport's.
        _runner = new BeaconSessionRunner(
            sessions, tasks, clock, wake, channels, relays, socks, ingest, tasking);
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

        var implant = ResolveIdentity(handshakeRequest, httpContext, ClientCertificateIdentity.Read(httpContext)).ImplantId;

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

        // The gRPC adapters for the transport-agnostic session loop: MoveNext
        // and Current become "the next frame or null on a clean close",
        // WriteAsync becomes the frame writer. The loop and its frame paths
        // (results ingest, dispatch push, staged pulls, channel input) are the
        // shared core's (BeaconSessionRunner); only the transport plumbing --
        // and the single-writer discipline gRPC demands of it -- is here.
        await _runner.RunAsync(
            sessionContext,
            async ct => await requestStream.MoveNext(ct) ? requestStream.Current : null,
            (frame, ct) => responseStream.WriteAsync(frame, ct),
            context.CancellationToken);
    }

    private async Task<(HandshakeResponse Response, HandshakeResult? Handshake)> TryHandshakeAsync(
        HttpContext httpContext,
        HandshakeRequest request)
    {
        // The certificate identity is authoritative (read off the mTLS-presented
        // cert), not the wire -- an implant cannot name another engagement by
        // editing its handshake. The implant id from the cert is what we look up.
        // The handshake-id fallback -- identity by id alone, the cleartext dev
        // posture a socket anything with reach can present -- fires only over
        // cleartext. Over TLS the certificate is the identity: a certificate-less
        // connection reached HTTP only because the single-port https listener
        // allows one (enrollment has no certificate to present yet), and it must
        // not be able to name an implant by id.
        var certIdentity = ClientCertificateIdentity.Read(httpContext);
        var resolved = ResolveIdentity(request, httpContext, certIdentity);

        try
        {
            var result = await _handshake.HandshakeAsync(
                new HandshakeCommand(
                    ImplantId: resolved.ImplantId,
                    MajorVersion: request.Version?.Major ?? -1,
                    MinorVersion: request.Version?.Minor ?? -1,
                    Capabilities: request.Capabilities,
                    CertificateEngagementId: resolved.CertificateEngagementId,
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

    // The identity the handshake runs under: the certificate binding when one
    // was presented; over cleartext only, the handshake-field fallback (the
    // dev posture, architecture.md Sec 8); over TLS with no certificate,
    // nothing -- the default id fails the handshake's implant lookup.
    private readonly record struct ResolvedIdentity(ImplantId ImplantId, EngagementId? CertificateEngagementId);

    private static ResolvedIdentity ResolveIdentity(
        HandshakeRequest request, HttpContext httpContext, ClientIdentity? certIdentity)
    {
        if (certIdentity is { } identity)
            return new(identity.ImplantId, identity.EngagementId);
        if (!httpContext.Request.IsHttps && ParseImplantId(request.ImplantId) is { } byId)
            return new(byId, null);
        return new(default, null);
    }

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
