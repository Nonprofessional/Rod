using System.Net;
using System.Net.Quic;
using System.Net.Security;
using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;
using Rod.Transport.Channels;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners.Streams;
using Rod.V1;
using Task = System.Threading.Tasks.Task;

namespace Rod.Transport.Listeners.Quic;

// The QUIC listener service (architecture.md Sec 8): the socket-owning
// family's duplex variant, for egress that passes UDP/443 -- where HTTP/3-era
// traffic lives -- but blocks TCP. One connection is one live session (not
// the one-connection-one-poll cadence the pipe and raw TCP serve): the
// implant opens a single bidirectional stream, speaks first with the
// handshake message, and the shared BeaconSessionRunner holds the session --
// server-push tasking the moment it is queued, live channels for the
// streaming verbs, the same native-channel tier the gRPC stream and the
// WebSocket beacon run. The message grammar is the stream check-in contract
// (extending/implants.md): one self-delimited message per direction turn --
// a varint byte length, then the envelope's delimited frame sequence.
//
// The TLS story is the web posture's (Sec 8/9): QUIC cannot ride cleartext,
// so the listener terminates TLS 1.3 with the CA-issued server leaf every
// TLS listener shares and requests no client certificate anywhere -- to a
// probe the front is an ordinary TLS-terminating UDP endpoint. The identity
// is therefore the socket-owning family's own: the implant id in the
// handshake, the certificate-less posture, with the enrolled, kill-date, and
// retired gates applying in full; dispatched tasking keeps the complete
// Sec 9 signature posture, verified by the implant exactly like a
// stream-delivered task.

/// <summary>
/// Binds the entry's UDP endpoint as a QUIC listener and serves live
/// check-in sessions until the host stops.
/// </summary>
internal sealed class QuicListenerService : BackgroundService
{
    /// <summary>
    /// The ALPN protocol id the listener matches and the implant dials with
    /// (extending/implants.md). The implant tree keeps a copy in textual
    /// lockstep, the same discipline the beacon URL shapes follow.
    /// </summary>
    public const string Alpn = "rod1";

    // Whether the host OS carries a QUIC stack (msquic on Linux): the
    // union guard the platform analyzer follows, so each entry point
    // refuses a platform that cannot run the transport instead of
    // throwing mid-startup.
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
    [System.Runtime.Versioning.SupportedOSPlatformGuard("osx")]
    private static bool QuicAvailable => QuicListener.IsSupported;

    // How long a connection gets to open its stream and speak its handshake:
    // a client that connects and goes silent must not pin a handler, the
    // same bound the poll bridge holds over a whole check-in.
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    // How long a connection may sit silent before the listener drops it. The
    // reference implant sends QUIC keep-alives while its session is parked,
    // so a silent connection is a dead peer, not a quiet one; without this
    // its half-open stream would hold a reader until the process ends.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly Listener _listener;
    private readonly HandshakeService _handshake;
    private readonly ISessionRegistry _sessions;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _clock;
    private readonly IImplantCertificateAuthority _ca;
    private readonly BeaconSessionRunner _runner;
    private readonly IListenerRegistry _listeners;
    private readonly ILogger<QuicListenerService> _logger;

    public QuicListenerService(
        Listener listener,
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
        BeaconTasking tasking,
        IImplantCertificateAuthority ca,
        IListenerRegistry listeners,
        ILogger<QuicListenerService> logger)
    {
        _listener = listener;
        _handshake = handshake;
        _sessions = sessions;
        _audit = audit;
        _clock = clock;
        _ca = ca;
        _runner = new BeaconSessionRunner(
            sessions, tasks, clock, wake, channels, relays, socks, ingest, tasking);
        _listeners = listeners;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!QuicAvailable)
            throw new InvalidOperationException(
                "The host provides no QUIC stack; the quic listener cannot bind "
                + "(install libmsquic on Linux, or run on an OS that ships one).");

        var (host, port) = TransportHost.ParseBindAddress(_listener.BindAddress);
        var certificate = _ca.GetServerCertificate();
        var alpn = new List<SslApplicationProtocol> { new(Alpn) };
        var options = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(host, port),
            ApplicationProtocols = alpn,
            ConnectionOptionsCallback = (_, _, _) =>
            {
                if (!QuicAvailable)
                    throw new InvalidOperationException(
                        "The host provides no QUIC stack; the connection is refused.");
                return ValueTask.FromResult(ServerConnectionOptions(certificate, alpn));
            },
        };
        var listener = await QuicListener.ListenAsync(options, stoppingToken);

        // Bind first, then register: the registry reflects what is actually
        // listening, the same ordering every transport follows.
        await _listeners.RegisterAsync(_listener, stoppingToken);

        _logger.LogInformation(
            "Rod QUIC listener {Name} answering stream check-ins on {Bind} for {Endpoint}.",
            _listener.Name, _listener.BindAddress, _listener.PublicEndpoint);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                QuicConnection connection;
                try
                {
                    connection = await listener.AcceptConnectionAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (QuicException)
                {
                    continue; // transient; the next accept retries
                }

                // One connection is one session; serve it off the accept
                // loop so concurrent sessions overlap.
                _ = Task.Run(() => ServeConnectionAsync(connection, stoppingToken), stoppingToken);
            }
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    // The per-connection options the callback hands each accepted connection.
    // A method of its own -- the callback lambda runs outside the bind-time
    // guard, so it carries its own.
    private static QuicServerConnectionOptions ServerConnectionOptions(
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        List<SslApplicationProtocol> alpn)
    {
        if (!QuicAvailable)
            throw new InvalidOperationException("The host provides no QUIC stack; the connection is refused.");

        return new QuicServerConnectionOptions
        {
            IdleTimeout = IdleTimeout,
            MaxInboundBidirectionalStreams = 1,
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ApplicationProtocols = alpn,
                // The web posture's fingerprint rule (Sec 8): no client
                // certificate is requested anywhere -- the ask itself is
                // what an IDS reads. The handshake carries the identity.
                ClientCertificateRequired = false,
            },
        };
    }

    // One connection: the implant's first bidirectional stream carries the
    // handshake message, then the session runs on it until either side ends.
    // Every failure -- a malformed message, a refused handshake, a vanished
    // client -- ends the connection; the next cycle reconnects, the cadence
    // the poll bridges already keep.
    private async Task ServeConnectionAsync(QuicConnection connection, CancellationToken stoppingToken)
    {
        if (!QuicAvailable)
            return;

        try
        {
            await using var _ = connection;

            // The handshake window bounds both halves of the opening: the
            // stream's arrival and the message on it. A client that connects
            // and goes silent must not pin a handler, the same bound the
            // poll bridge holds over a whole check-in.
            using var handshakeWindow = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            handshakeWindow.CancelAfter(HandshakeTimeout);
            QuicStream stream;
            List<Frame> frames;
            try
            {
                stream = await connection.AcceptInboundStreamAsync(handshakeWindow.Token);
                frames = EnvelopeFraming.Parse(
                    await StreamCheckInFraming.ReadMessageAsync(stream, handshakeWindow.Token));
            }
            catch (Exception ex) when (
                ex is EnvelopeFramingException or IOException or OperationCanceledException)
            {
                // A malformed or oversized message gets no answer: the
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
                await RespondAsync(
                    stream,
                    Response(HandshakeStatus.Unspecified, engagementId: null, replayNonces: false),
                    stoppingToken);
                return;
            }

            var (response, handshake) = await TryHandshakeAsync(implantId, handshakeRequest);
            if (response.Status != HandshakeStatus.Ok || handshake is null)
            {
                await RespondAsync(stream, response, stoppingToken);
                return;
            }

            // A genuinely new session is recorded; a reused one (every
            // reconnect after the first) is not, the same flood guard every
            // transport applies (architecture.md Sec 10.3, Sec 11).
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
                handshakeRequest.Capabilities,
                handshake.TaskAcks);

            // One presence touch, then the session guard: if the session this
            // handshake holds was closed out from under it, stop after the
            // handshake response so the implant re-handshakes on its reconnect.
            await _sessions.TouchAsync(session.Implant, session.Capabilities, _clock.GetUtcNow(), stoppingToken);
            var active = await _sessions.GetActiveAsync(session.Implant, stoppingToken);
            await RespondAsync(stream, response, stoppingToken);
            if (active is null || active.Id != session.SessionId)
                return;

            // The live session over message adapters: each inbound message is
            // the envelope's delimited frame sequence (the frames beyond the
            // handshake of the first message count here), each outbound frame
            // leaves as its own message -- the push shape the runner runs.
            var pending = new Queue<Frame>(frames.Skip(1));
            await _runner.RunAsync(
                session,
                async cancellationToken =>
                {
                    while (pending.Count == 0)
                    {
                        var message = await StreamCheckInFraming.ReadMessageAsync(stream, cancellationToken);
                        foreach (var frame in EnvelopeFraming.Parse(message))
                            pending.Enqueue(frame);
                    }
                    return pending.Dequeue();
                },
                (frame, cancellationToken) => StreamCheckInFraming.WriteMessageAsync(
                    stream, EnvelopeFraming.Encode(new[] { frame }), cancellationToken),
                stoppingToken);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException
            or IOException
            or ObjectDisposedException
            or QuicException)
        {
            // The client vanished or the host is stopping: the connection
            // ends, and the next cycle reconnects. A dispatched task whose
            // frame write failed returns to the queue inside the runner --
            // the same retransmission tolerance every transport carries.
        }
    }

    private static async Task RespondAsync(QuicStream stream, HandshakeResponse response, CancellationToken stoppingToken)
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
                    ReplayNonces: request.ReplayNonces,
                    TaskAcks: request.TaskAcks),
                CancellationToken.None);
            return (Response(
                HandshakeStatus.Ok, result.EngagementId.ToString(),
                result.ReplayNonces, result.TaskAcks), result);
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

    private static HandshakeResponse Response(
        HandshakeStatus status, string? engagementId, bool replayNonces, bool taskAcks = false)
        => new()
        {
            Status = status,
            Version = new ProtocolVersion { Major = ProtocolVersions.Major, Minor = ProtocolVersions.Minor },
            EngagementId = engagementId ?? string.Empty,
            ReplayNonces = replayNonces,
            TaskAcks = taskAcks,
        };

    private static Frame HandshakeFrame(HandshakeResponse response)
        => new() { Payload = ByteString.CopyFrom(response.ToByteArray()) };
}
