using System.Net;
using System.Net.Sockets;
using System.Text;
using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

/// <summary>
/// Checks of the tunnel.forward handler (architecture.md Sec 5.2, Sec 14,
/// Sec 10.3): the channel bridges a TCP connection of the implant's own --
/// operator input is relayed to the peer, the peer's answers stream back as
/// output chunks, eof half-closes the send side, and the tunnel ends when the
/// peer closes or with the stream that carries it. Drives the handler against
/// a fake IChannelStream and a loopback echo peer standing in for the third
/// host; the wire behavior is the server-side integration suite's subject.
/// </summary>
public class TunnelForwardTests
{
    [Fact]
    public async Task RunAsync_RelaysBothWays_AndEndsWhenThePeerCloses()
    {
        await using var peer = EchoPeer.Start();
        var channel = new FakeChannel();
        var runner = TunnelForward.RunAsync($"127.0.0.1 {peer.Port}", channel, CancellationToken.None);

        // The operator's bytes cross the tunnel and the peer's answer lands on
        // the channel's output -- the whole point of the bridge.
        await channel.SendAsync("ping");
        await channel.WaitUntilOutputContainsAsync("ping");

        // The operator's eof half-closes the tunnel's send side; the peer ends
        // its side in answer, and that close is the task's natural end.
        await channel.SendEofAsync();
        var (outcome, output) = await runner.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("ping", channel.OutputText);
        Assert.Contains("relayed 4 bytes up, 4 bytes down", output);
    }

    [Fact]
    public async Task RunAsync_RefusesAPeerThatWillNotConnect()
    {
        // A port with no listener: the tunnel is refused on the task itself --
        // the operator sees the cause where they look for the outcome.
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;
        taken.Stop();

        var channel = new FakeChannel();
        var (outcome, output) = await TunnelForward.RunAsync($"127.0.0.1 {port}", channel, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("connect to 127.0.0.1:", output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("justhost")]
    [InlineData("host port")]
    [InlineData("127.0.0.1 notaport")]
    [InlineData("127.0.0.1 0")]
    [InlineData("127.0.0.1 65536")]
    public async Task RunAsync_RefusesArgumentsOutsideTheGrammar(string arguments)
    {
        var channel = new FakeChannel();
        var (outcome, output) = await TunnelForward.RunAsync(arguments, channel, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Equal("tunnel.forward expects '<host> <port>'", output);
    }

    [Fact]
    public async Task RunAsync_DiesWithTheStreamThatCarriesIt()
    {
        await using var peer = EchoPeer.Start();
        var channel = new FakeChannel();
        using var gone = new CancellationTokenSource();

        // The tunnel has proven itself live (the peer answered), then the
        // beacon stream dies: the channel is session-scoped, and the tunnel
        // ends with it rather than outliving its carrier.
        var runner = TunnelForward.RunAsync($"127.0.0.1 {peer.Port}", channel, gone.Token);
        await channel.SendAsync("ping");
        await channel.WaitUntilOutputContainsAsync("ping");
        gone.Cancel();

        var (outcome, output) = await runner.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("channel closed", output);
    }

    /// <summary>
    /// A fake channel: collects the output chunks the handler streams and
    /// hands the handler input on demand, so the tests drive a live tunnel's
    /// ordering (send, read the answer, close) without a wire.
    /// </summary>
    private sealed class FakeChannel : IChannelStream
    {
        private readonly System.Threading.Channels.Channel<(byte[]? Data, bool Eof)> _input =
            System.Threading.Channels.Channel.CreateUnbounded<(byte[]? Data, bool Eof)>();

        private readonly List<byte[]> _output = new();

        public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            lock (_output)
            {
                _output.Add(data.ToArray());
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<(byte[]? Data, bool Eof)> ReadInputAsync(CancellationToken cancellationToken)
            => _input.Reader.ReadAsync(cancellationToken);

        public string OutputText
        {
            get
            {
                lock (_output)
                {
                    return Encoding.UTF8.GetString(_output.SelectMany(c => c).ToArray());
                }
            }
        }

        public Task SendAsync(string text)
            => _input.Writer.WriteAsync((Encoding.UTF8.GetBytes(text), false)).AsTask();

        public Task SendEofAsync()
            => _input.Writer.WriteAsync((Array.Empty<byte>(), true)).AsTask();

        // Waits until the transcript carries the marker -- the observable
        // effect of the bytes the test sent down the tunnel.
        public async Task WaitUntilOutputContainsAsync(string marker, TimeSpan? deadline = null)
        {
            var until = DateTimeOffset.UtcNow + (deadline ?? TimeSpan.FromSeconds(10));
            while (DateTimeOffset.UtcNow < until)
            {
                if (OutputText.Contains(marker, StringComparison.Ordinal))
                    return;
                await Task.Delay(25);
            }
            Assert.Fail($"timed out waiting for the tunnel to relay '{marker}' back");
        }
    }

    /// <summary>
    /// The third host of these tests: a loopback TCP listener that echoes every
    /// byte back until its peer half-closes, then ends its own side -- the
    /// close that ends the tunnel.
    /// </summary>
    private sealed class EchoPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serve;

        private EchoPeer(TcpListener listener, Task serve)
        {
            _listener = listener;
            _serve = serve;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public static EchoPeer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new EchoPeer(listener, ServeAsync(listener));
        }

        private static async Task ServeAsync(TcpListener listener)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync();
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            using (socket)
            {
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var received = 0;
                    try
                    {
                        received = await socket.ReceiveAsync(buffer, SocketFlags.None);
                    }
                    catch (SocketException)
                    {
                        return;
                    }
                    if (received <= 0)
                        return;
                    var sent = 0;
                    while (sent < received)
                        sent += await socket.SendAsync(
                            buffer.AsMemory(sent, received - sent), SocketFlags.None);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }
}
