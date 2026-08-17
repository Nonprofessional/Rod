namespace Rod.Transport.Listeners.Dns;

// The DNS wire codec the DNS listener speaks (architecture.md Sec 8): the
// minimal subset a TXT check-in exchange needs -- one question, TXT answers,
// and an EDNS0 OPT record so a signed TaskRequest fits the response. Hand-rolled
// on purpose: the codec is ~200 lines, the DNS message format is stable since
// RFC 1035, and the layer stays package-free the way the inner rings are
// (a full DNS library would drag a dependency in for records this listener
// never serves).

/// <summary>
/// One decoded/encoded DNS message. Queries carry a single TXT question;
/// responses echo it and carry zero or one TXT answer plus an OPT record
/// advertising the response-size budget. Names are written uncompressed and
/// parsed with compression-pointer support so a forwarding resolver's
/// compressed question still decodes.
/// </summary>
internal sealed class DnsMessage
{
    public ushort Id { get; set; }

    /// <summary>True for a response (the QR bit). Queries leave it false.</summary>
    public bool IsResponse { get; set; }

    /// <summary>The single question, null on a malformed parse.</summary>
    public DnsQuestion? Question { get; set; }

    /// <summary>TXT answers to write on encode; empty on queries.</summary>
    public List<DnsTxtAnswer> Answers { get; } = new();

    /// <summary>
    /// The EDNS0 UDP payload size: read from a query's OPT record (what the
    /// client accepts), written on responses (what this listener may send).
    /// Zero when absent.
    /// </summary>
    public ushort UdpPayloadSize { get; set; }

    /// <summary>The response code to write (0 NOERROR, 3 NXDOMAIN).</summary>
    public ushort ResponseCode { get; set; }
}

internal sealed record DnsQuestion(string Name, ushort Type, ushort Class);

/// <summary>One TXT answer record; the strings concatenate into the payload.</summary>
internal sealed record DnsTxtAnswer(string Name, IReadOnlyList<string> Strings);

internal static class DnsCodec
{
    public const ushort TxtType = 16;
    public const ushort OptType = 41;
    private const ushort InClass = 1;

    // What this listener advertises it may send: the modern safe ceiling
    // (1232, the DNS Flag Day 2020 number) that fits a signed TaskRequest with
    // room for the DNS headers.
    public const ushort ResponsePayloadSize = 1232;

    /// <summary>
    /// Parses a query: the header, the single question, and the query's EDNS0
    /// payload size if present. Answer/authority/additional sections beyond
    /// the OPT record are skipped. Returns null when the datagram is not a
    /// parsable query with exactly one question.
    /// </summary>
    public static DnsMessage? ParseQuery(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 12)
            return null;

        var id = (ushort)((datagram[0] << 8) | datagram[1]);
        var flags = (ushort)((datagram[2] << 8) | datagram[3]);
        var qdcount = (ushort)((datagram[4] << 8) | datagram[5]);
        var ancount = (ushort)((datagram[6] << 8) | datagram[7]);
        var nscount = (ushort)((datagram[8] << 8) | datagram[9]);
        var arcount = (ushort)((datagram[10] << 8) | datagram[11]);

        // QR set means this datagram is a response, not a query.
        if ((flags & 0x8000) != 0)
            return null;
        if (qdcount != 1)
            return null;

        var offset = 12;
        if (!TryReadName(datagram, ref offset, out var name))
            return null;
        if (offset + 4 > datagram.Length)
            return null;
        var type = (ushort)((datagram[offset] << 8) | datagram[offset + 1]);
        var @class = (ushort)((datagram[offset + 2] << 8) | datagram[offset + 3]);
        offset += 4;

        var message = new DnsMessage
        {
            Id = id,
            Question = new DnsQuestion(name, type, @class),
        };

        // Skip the remaining sections; scan the additional section for an OPT
        // record to learn the client's UDP payload budget.
        for (var section = 0; section < ancount + nscount + arcount; section++)
        {
            var isAdditional = section >= ancount + nscount;
            if (!TryReadName(datagram, ref offset, out var recordName))
                return message;
            if (offset + 10 > datagram.Length)
                return message;
            var rtype = (ushort)((datagram[offset] << 8) | datagram[offset + 1]);
            var rclass = (ushort)((datagram[offset + 2] << 8) | datagram[offset + 3]);
            var rdlength = (ushort)((datagram[offset + 8] << 8) | datagram[offset + 9]);
            offset += 10;
            if (offset + rdlength > datagram.Length)
                return message;

            // The OPT record: root name, type 41; its CLASS is the advertised
            // UDP payload size.
            if (isAdditional && rtype == OptType && recordName.Length == 0)
                message.UdpPayloadSize = rclass;

            offset += rdlength;
        }

        return message;
    }

    /// <summary>
    /// Encodes a response: the echoed question, the TXT answers, and an OPT
    /// record advertising the response budget. Names are written uncompressed.
    /// </summary>
    public static byte[] EncodeResponse(DnsMessage message)
    {
        var buffer = new List<byte>(512);

        // Header: id, QR|AA|RA set, the response code; one question, the
        // answers, no authority, one additional (the OPT record).
        WriteU16(buffer, message.Id);
        WriteU16(buffer, (ushort)(0x8000 | 0x0400 | 0x0080 | (message.ResponseCode & 0x000F)));
        WriteU16(buffer, message.Question is null ? (ushort)0 : (ushort)1);
        WriteU16(buffer, (ushort)message.Answers.Count);
        WriteU16(buffer, 0);
        WriteU16(buffer, 1);

        if (message.Question is { } question)
        {
            WriteName(buffer, question.Name);
            WriteU16(buffer, question.Type);
            WriteU16(buffer, question.Class);
        }

        foreach (var answer in message.Answers)
        {
            WriteName(buffer, answer.Name);
            WriteU16(buffer, TxtType);
            WriteU16(buffer, InClass);
            WriteU32(buffer, 0); // TTL: answers are per-check-in, never cached
            var rdata = new List<byte>(answer.Strings.Sum(s => s.Length + 1));
            foreach (var s in answer.Strings)
            {
                if (s.Length > 255)
                    throw new InvalidOperationException("A TXT string exceeds the 255-byte record limit.");
                rdata.Add((byte)s.Length);
                rdata.AddRange(System.Text.Encoding.ASCII.GetBytes(s));
            }
            WriteU16(buffer, (ushort)rdata.Count);
            buffer.AddRange(rdata);
        }

        // The OPT record (EDNS0): root name, type 41, CLASS = the payload
        // budget, no extended flags, no data.
        buffer.Add(0);
        WriteU16(buffer, OptType);
        WriteU16(buffer, message.UdpPayloadSize is > 0 ? message.UdpPayloadSize : ResponsePayloadSize);
        WriteU32(buffer, 0);
        WriteU16(buffer, 0);

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads a (possibly compressed) domain name. Follows compression pointers
    /// with a jump budget so a malicious loop cannot spin: RFC 1035 pointers
    /// only ever point backwards, so the walk is bounded by the datagram.
    /// </summary>
    private static bool TryReadName(ReadOnlySpan<byte> datagram, ref int offset, out string name)
    {
        var labels = new List<string>();
        var jumps = 0;
        var cursor = offset;
        var nextAfterPointer = -1;
        while (true)
        {
            if (cursor >= datagram.Length)
            {
                name = "";
                return false;
            }
            var length = datagram[cursor];
            if (length == 0)
            {
                cursor++;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= datagram.Length || ++jumps > datagram.Length)
                {
                    name = "";
                    return false;
                }
                var pointer = ((length & 0x3F) << 8) | datagram[cursor + 1];
                if (nextAfterPointer < 0)
                    nextAfterPointer = cursor + 2;
                cursor = pointer;
                continue;
            }
            if (cursor + 1 + length > datagram.Length)
            {
                name = "";
                return false;
            }
            labels.Add(System.Text.Encoding.ASCII.GetString(datagram.Slice(cursor + 1, length)).ToLowerInvariant());
            cursor += 1 + length;
        }
        offset = nextAfterPointer >= 0 ? nextAfterPointer : cursor;
        name = string.Join('.', labels);
        return true;
    }

    private static void WriteName(List<byte> buffer, string name)
    {
        if (name.Length == 0)
        {
            buffer.Add(0);
            return;
        }
        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63)
                throw new InvalidOperationException($"DNS label '{label}' is empty or over the 63-byte limit.");
            buffer.Add((byte)label.Length);
            buffer.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
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
