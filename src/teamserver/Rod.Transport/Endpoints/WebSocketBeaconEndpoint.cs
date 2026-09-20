using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Pki;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;
using Rod.Transport.Channels;
using Rod.Transport.Payloads;
using Rod.V1;
// The domain entity shares its name with the BCL Task; this file uses the
// Tasks namespace for the service types but never the entity by name, so pin
// Task to the BCL type the signatures need.
using Task = System.Threading.Tasks.Task;

namespace Rod.Transport.Endpoints;

// The WebSocket beacon stream (architecture.md Sec 8, the web posture's
// interactive tier): the same tasking session the gRPC stream runs, over a
// WebSocket on the plain-HTTP listener family -- so an implant whose only
// front is a web front can still hold the live channel (shell.interact,
// tunnel.forward, tunnel.socks) without faking it with back-to-back polls.
// The handshake, the auth, and the frame grammar are the envelope contact's
// own, message-shaped instead of body-shaped: every WebSocket message carries
// the same sealed-or-plaintext framed-frames body the envelope POST does, so
// the web transports keep their posture -- the per-artifact key authenticates
// and seals, an https or cleartext front leaks no frame bytes in either
// direction -- and an implant that speaks the envelope already speaks
// everything but the socket.

/// <summary>
/// Maps the WebSocket beacon route. Mapped alongside the operator API and the
/// envelope contact on every listener like them; the identity rules are the
/// envelope's (the sealed body's artifact key over the web posture, the
/// cleartext id fallback in the lab posture only).
/// </summary>
public static class WebSocketBeaconEndpoints
{
    /// <summary>The WebSocket beacon route, in the implant family with enroll and the envelope.</summary>
    public const string Route = "/implants/beacon/stream";

    public static IEndpointRouteBuilder MapWebSocketBeaconEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, async (
            HttpContext http,
            WebSocketBeaconStream stream,
            CancellationToken cancellationToken)
            => await stream.HandleAsync(http, cancellationToken))
            .WithName(nameof(WebSocketBeaconStream));
        return endpoints;
    }
}

/// <summary>
/// One WebSocket beacon stream. The session loop is the shared
/// <see cref="BeaconSessionRunner"/> the gRPC endpoint runs; this class owns
/// only the transport plumbing -- the message receive loop (unwrap each
/// sealed message, parse its frames) and the message send (one frame per
/// message, sealed the same way the envelope seals its responses).
/// </summary>
internal sealed class WebSocketBeaconStream
{
    // One message's frame budget: the envelope's wire-body cap governs a POST
    // body; a WebSocket message gets the same ceiling here so a runaway
    // client cannot pin memory a POST could not.
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private static readonly ReadOnlyMemory<byte> CounterZero = new byte[8];

    private readonly HandshakeService _handshake;
    private readonly ISessionRegistry _sessions;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _clock;
    private readonly IPayloadStore _payloads;
    private readonly EnvelopeContactKeys _contactKeys;
    private readonly BeaconSessionRunner _runner;

    public WebSocketBeaconStream(
        HandshakeService handshake,
        ISessionRegistry sessions,
        TaskService tasks,
        IAuditStore audit,
        TimeProvider clock,
        ITaskDispatchWake wake,
        LiveChannelHub channels,
        Rod.Transport.Channels.DegradedChannelHub degraded,
        TaskRelayHub relays,
        SocksProxyHub socks,
        BeaconIngest ingest,
        BeaconTasking tasking,
        IPayloadStore payloads,
        EnvelopeContactKeys contactKeys)
    {
        _handshake = handshake;
        _sessions = sessions;
        _audit = audit;
        _clock = clock;
        _payloads = payloads;
        _contactKeys = contactKeys;
        _runner = new BeaconSessionRunner(
            sessions, tasks, clock, wake, channels, degraded, relays, socks, ingest, tasking);
    }

    public async Task HandleAsync(HttpContext http, CancellationToken cancellationToken)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var ws = await http.WebSockets.AcceptWebSocketAsync();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 1. The first message is the contact body the envelope route reads:
        //    the sealed envelope under the per-artifact key, or the plaintext
        //    framed frames of the lab posture.
        var first = await ReceiveMessageAsync(ws, linked.Token);
        if (first is null)
            return;

        long contactCounter = 0;
        var sealedKey = (KeyId: Guid.Empty, Key: Array.Empty<byte>());
        var isSealed = false;
        var framed = first;
        if (EnvelopeBeaconContact.TryReadSealedKeyId(first, out var sealedText) is { } keyId)
        {
            var carrier = await _payloads.FindByEnvelopeKeyAsync(keyId, linked.Token);
            if (carrier?.EnvelopeKey is not { } artifactKey
                || AesGcmEnvelope.TryUnwrap(sealedText, keyId, artifactKey, AesGcmEnvelope.ContactRequestAad)
                    is not { } plaintext
                || plaintext.Length < CounterZero.Length)
            {
                // The unwrap failed, so there is nothing to seal a refusal
                // under: the raw unspecified refusal leaks only the status.
                await SendFramesAsync(ws, sealedBody: false,
                    new[] { EnvelopeBeaconContact.HandshakeFrame(
                        BeaconHandshake.Response(HandshakeStatus.Unspecified, engagementId: null, replayNonces: false)) },
                    sealedKey, linked.Token);
                return;
            }
            contactCounter = BinaryPrimitives.ReadInt64BigEndian(plaintext);
            framed = plaintext[CounterZero.Length..];
            sealedKey = (keyId, artifactKey);
            isSealed = true;
        }

        // 2. The handshake -- the same parse, gates, and identity rules the
        //    envelope applies, because this is the same posture on a different
        //    socket.
        List<Frame> frames;
        try
        {
            frames = EnvelopeFraming.Parse(framed);
        }
        catch (EnvelopeFramingException)
        {
            return; // A malformed body gets no answer; the connection ends.
        }

        if (frames.Count == 0 || !EnvelopeBeaconContact.TryParseHandshake(frames[0], out var handshakeRequest))
        {
            await SendFramesAsync(ws, isSealed, new[] { EnvelopeBeaconContact.HandshakeFrame(
                BeaconHandshake.Response(HandshakeStatus.Unspecified, engagementId: null, replayNonces: false)) },
                sealedKey, linked.Token);
            return;
        }

        // The key posture gates, the envelope's own: a key-bound implant must
        // arrive sealed under exactly its bound key, and the counter must
        // clear the accepted floor.
        if (ImplantId.TryParse(handshakeRequest.ImplantId, out var sealedImplant))
        {
            if (_contactKeys.TryGet(sealedImplant) is { } bound)
            {
                if (!isSealed || sealedKey.KeyId != bound.KeyId)
                {
                    await SendFramesAsync(ws, isSealed, new[] { EnvelopeBeaconContact.HandshakeFrame(
                        BeaconHandshake.Response(HandshakeStatus.Unspecified, engagementId: null, replayNonces: false)) },
                        sealedKey, linked.Token);
                    return;
                }
            }
            if (isSealed && !_contactKeys.Accept(sealedImplant, contactCounter))
            {
                await SendFramesAsync(ws, isSealed, new[] { EnvelopeBeaconContact.HandshakeFrame(
                    BeaconHandshake.Response(HandshakeStatus.Unspecified, engagementId: null, replayNonces: false)) },
                    sealedKey, linked.Token);
                return;
            }
        }

        // The identity for the handshake: the certificate binding when the
        // transport presented one, else the sealed body's posture -- which
        // stands by reach alone only in the cleartext lab shape.
        var identity = ClientCertificateIdentity.Read(http);
        var (response, handshake) = await EnvelopeBeaconContact.TryHandshakeAsync(
            _handshake, identity, handshakeRequest, isSealed || !http.Request.IsHttps);
        await SendFramesAsync(ws, isSealed, new[] { EnvelopeBeaconContact.HandshakeFrame(response) },
            sealedKey, linked.Token);
        if (response.Status != HandshakeStatus.Ok || handshake is null)
            return;

        // A genuinely new session is recorded; a reused one is not -- the same
        // flood guard the envelope and the gRPC stream apply
        // (architecture.md Sec 10.3, Sec 11).
        await BeaconHandshake.AppendSessionOpenedAsync(_audit, handshake, handshakeRequest);

        var session = new BeaconSessionContext(
            handshake.ImplantId,
            handshake.EngagementId,
            handshake.SessionId,
            handshake.DeployedBy,
            handshakeRequest.Capabilities,
            handshake.TaskAcks);

        // The same session guard the envelope applies: a session closed out
        // from under this handshake ends the stream before the loop starts,
        // so the implant re-handshakes on its reconnect.
        await _sessions.TouchAsync(session.Implant, session.Capabilities, _clock.GetUtcNow(), "web", linked.Token);
        var active = await _sessions.GetActiveAsync(session.Implant, linked.Token);
        if (active is null || active.Id != session.SessionId)
            return;

        // 3. The live session: the shared runner over message adapters. Each
        //    message unwraps (and floor-checks its counter) exactly like the
        //    envelope's request body; each frame leaves as its own message,
        //    sealed when the session sealed.
        var pending = new Queue<Frame>();
        await _runner.RunAsync(
            session,
            async token =>
            {
                while (pending.Count == 0)
                {
                    var message = await ReceiveMessageAsync(ws, token);
                    if (message is null)
                        return null;
                    byte[] plaintext;
                    if (isSealed)
                    {
                        if (EnvelopeBeaconContact.TryReadSealedKeyId(message, out var messageSealed) is not { } messageKey
                            || messageKey != sealedKey.KeyId
                            || AesGcmEnvelope.TryUnwrap(messageSealed, sealedKey.KeyId, sealedKey.Key, AesGcmEnvelope.ContactRequestAad)
                                is not { } unsealed
                            || unsealed.Length < CounterZero.Length
                            || !_contactKeys.Accept(session.Implant, BinaryPrimitives.ReadInt64BigEndian(unsealed)))
                            throw new InvalidOperationException("A sealed message did not verify under its artifact key.");
                        plaintext = unsealed[CounterZero.Length..];
                    }
                    else
                    {
                        plaintext = message;
                    }
                    foreach (var frame in EnvelopeFraming.Parse(plaintext))
                        pending.Enqueue(frame);
                }
                return pending.Dequeue();
            },
            (frame, token) => SendFramesAsync(ws, isSealed, new[] { frame }, sealedKey, token),
            cancellationToken);
    }

    // One message in: fragments accumulate until EndOfMessage; null on a clean
    // client close, the transport's own exception on an abort.
    private static async Task<byte[]?> ReceiveMessageAsync(WebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                return null;
            message.Write(buffer, 0, received.Count);
            if (message.Length > MaxMessageBytes)
                throw new EnvelopeFramingException(oversized: true);
            if (received.EndOfMessage)
                return message.ToArray();
        }
    }

    // One frame-batch out: sealed under the artifact key when the session
    // sealed (a text message carrying the base64 envelope, the same shape the
    // envelope route replies with), raw binary otherwise.
    private static Task SendFramesAsync(
        WebSocket ws, bool sealedBody, IReadOnlyList<Frame> frames,
        (Guid KeyId, byte[] Key) sealedKey, CancellationToken cancellationToken)
    {
        var payload = sealedBody
            ? Encoding.UTF8.GetBytes(AesGcmEnvelope.Wrap(
                EnvelopeFraming.Encode(frames), sealedKey.KeyId, sealedKey.Key, AesGcmEnvelope.ContactResponseAad))
            : EnvelopeFraming.Encode(frames);
        return ws.SendAsync(
            payload,
            sealedBody ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken);
    }
}
