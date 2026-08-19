using System.IO;
using Rod.Transport.Endpoints;

namespace Rod.Transport.Listeners.Streams;

// The stream check-in framing (architecture.md Sec 8): the self-delimited
// message shape the named-pipe and raw-TCP listeners carry. One check-in is
// one message in each direction -- a varint byte length followed by exactly
// that many envelope-body bytes (the same varint-length-delimited Frame
// sequence the plain-HTTP envelope rides, EnvelopeFraming). The length prefix
// is what a raw stream lacks that an HTTP body has for free: a boundary, so
// neither side guesses where the check-in ends and no half-close convention
// is imposed on pipes that have none.

/// <summary>
/// Reads and writes one self-delimited check-in message over a duplex stream.
/// The body is the envelope's delimited frame sequence, bounded by the same
/// budget the envelope enforces.
/// </summary>
internal static class StreamCheckInFraming
{
    /// <summary>
    /// Reads one check-in message: the varint length prefix, then exactly that
    /// many body bytes. A malformed varint, an over-budget length, or a stream
    /// that ends mid-message throws -- the caller drops the connection.
    /// </summary>
    public static async Task<byte[]> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        long length = 0;
        var shift = 0;
        while (true)
        {
            var read = await ReadOneByteAsync(stream, cancellationToken);
            length |= (long)(read & 0x7f) << shift;
            if ((read & 0x80) == 0)
                break;
            shift += 7;
            if (shift > 28)
                throw new IOException("Check-in length prefix is a malformed varint.");
        }

        if (length > EnvelopeFraming.MaxBodyBytes)
            throw new IOException("Check-in message exceeds the body budget.");

        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var chunk = await stream.ReadAsync(body.AsMemory(offset), cancellationToken);
            if (chunk <= 0)
                throw new EndOfStreamException();
            offset += chunk;
        }
        return body;
    }

    /// <summary>
    /// Writes one check-in message: the varint length prefix, then the body,
    /// then a flush so the peer's read completes without waiting on buffer
    /// boundaries.
    /// </summary>
    public static async Task WriteMessageAsync(Stream stream, byte[] body, CancellationToken cancellationToken)
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

        await stream.WriteAsync(prefix.AsMemory(0, index), cancellationToken);
        if (body.Length > 0)
            await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<int> ReadOneByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
        if (read <= 0)
            throw new EndOfStreamException();
        return buffer[0];
    }
}
