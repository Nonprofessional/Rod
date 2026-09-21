using System.Net.WebSockets;
using Google.Protobuf;
using Rod.V1;
using Xunit;

namespace Rod.Integration.Tests;

/// <summary>
/// The shared fake implant over the WebSocket beacon (the product's own
/// live-session carriage, architecture.md Sec 8): a plain ClientWebSocket
/// speaking the envelope's framed-frames messages against the plain-HTTP
/// listener, with the contract-faithful handshake. The round-trip harnesses
/// the mTLS gRPC stream once carried migrate here -- the WebSocket beacon
/// runs the same session runner, so every downstream assertion (transcripts,
/// audit arcs, tasking shapes) transfers unchanged.
/// </summary>
internal sealed class WsBeaconClient : IDisposable
{
    private readonly WebSocket _ws;

    private WsBeaconClient(WebSocket ws) => _ws = ws;

    /// <summary>
    /// Connects to the beacon route on the given plain-HTTP port and speaks
    /// first: the handshake frame advertising the capabilities the test's
    /// implant carries. The negotiation arms default off -- the bare
    /// handshake the round-trip harnesses always sent -- and a test pinning
    /// an arm names it.
    /// </summary>
    public static async Task<WsBeaconClient> ConnectAsync(
        int httpPort, string implantId, IReadOnlyList<string>? capabilities = null,
        bool replayNonces = false, bool taskAcks = false)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(
            new Uri($"ws://127.0.0.1:{httpPort}/implants/beacon/stream"), CancellationToken.None);
        var client = new WsBeaconClient(ws);
        var handshake = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1 },
            ImplantId = implantId,
            ReplayNonces = replayNonces,
            TaskAcks = taskAcks,
        };
        if (capabilities is { Count: > 0 })
            handshake.Capabilities.Add(capabilities);
        else
            handshake.Capabilities.Add("shell.exec");
        await client.SendFramesAsync(new[]
        {
            new Frame { Payload = ByteString.CopyFrom(handshake.ToByteArray()) },
        });
        return client;
    }

    public async Task<HandshakeResponse> ReceiveHandshakeAsync()
        => HandshakeResponse.Parser.ParseFrom(await ReceiveSingleFrameAsync());

    /// <summary>
    /// One downstream frame: the session runner writes one frame per message,
    /// so a read is a frame. <paramref name="expectKind"/> asserts the
    /// kind-bearing shapes (channel input) when the test waits on one.
    /// </summary>
    public async Task<byte[]> ReceiveSingleFrameAsync(FrameKind expectKind = FrameKind.Unspecified)
    {
        var frames = Parse(await ReceiveMessageAsync());
        Assert.Single(frames);
        if (expectKind != FrameKind.Unspecified)
            Assert.Equal(expectKind, frames[0].Kind);
        return frames[0].Payload.ToByteArray();
    }

    /// <summary>
    /// Reads frames until one parses as the awaited kind and passes the
    /// predicate -- the scanning shape the gRPC-era helpers used, kept so a
    /// migrated harness reads the same way (a channel input racing the
    /// dispatch, never before it).
    /// </summary>
    public async Task<T> ReceiveUntilAsync<T>(
        Func<Frame, T?> match, string awaiting, TimeSpan? deadline = null)
        where T : class
    {
        var end = DateTimeOffset.UtcNow + (deadline ?? TimeSpan.FromSeconds(30));
        while (DateTimeOffset.UtcNow < end)
        {
            foreach (var frame in Parse(await ReceiveMessageAsync()))
            {
                if (match(frame) is { } hit)
                    return hit;
            }
        }
        throw new TimeoutException($"Timed out waiting for the downstream {awaiting} frame.");
    }

    public async Task SendFramesAsync(IReadOnlyList<Frame> frames)
    {
        await _ws.SendAsync(
            Encode(frames), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
    }

    public void Dispose() => _ws.Dispose();

    private async Task<byte[]> ReceiveMessageAsync()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var received = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("The beacon stream closed under the test.");
            message.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
                return message.ToArray();
        }
    }

    // The envelope codec: the protobuf canonical delimited-stream shape --
    // an unsigned varint length before each marshaled Frame.

    private static byte[] Encode(IReadOnlyList<Frame> frames)
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

    private static List<Frame> Parse(byte[] body)
    {
        var frames = new List<Frame>();
        var position = 0;
        while (position < body.Length)
        {
            uint length = 0;
            var shift = 0;
            int delimiter;
            for (delimiter = 0; delimiter < 5; delimiter++)
            {
                var b = body[position + delimiter];
                length |= (uint)(b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                    break;
                shift += 7;
            }
            position += delimiter + 1;
            frames.Add(Frame.Parser.ParseFrom(body, position, (int)length));
            position += (int)length;
        }
        return frames;
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
