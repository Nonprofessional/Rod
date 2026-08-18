using System.Text;
using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

/// <summary>
/// Checks of the interactive shell handler (architecture.md Sec 10.3, the
/// streaming task shape): the channel pumps the platform shell's stdio --
/// the optional initial command and everything the operator types flow to the
/// shell, everything the shell prints streams back as output chunks -- and
/// the channel ends on eof, on the shell's own exit, or with the stream that
/// carries it. Drives the handler against a fake IChannelStream; the real
/// channel's wire behavior is the server-side integration suite's subject.
/// </summary>
public class InteractiveShellTests
{
    [Fact]
    public async Task RunAsync_StreamsInitialCommandOutput_AndEndsOnEof()
    {
        var channel = new FakeChannel();
        var runner = InteractiveShell.RunAsync("echo initial-marker", channel, CancellationToken.None);

        // Operator types after the initial command, then closes stdin: the
        // shell runs both commands and exits on its stdin's EOF.
        await channel.SendAsync("echo typed-marker\n");
        await channel.SendEofAsync();

        var (outcome, output) = await runner.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("initial-marker", channel.OutputText);
        Assert.Contains("typed-marker", channel.OutputText);
        Assert.Contains("shell exited", output);
    }

    [Fact]
    public async Task RunAsync_StreamsTypedInput_AsItArrives()
    {
        var channel = new FakeChannel();
        var runner = InteractiveShell.RunAsync("", channel, CancellationToken.None);

        // The channel is live before any input: type one command, confirm it
        // reached the transcript, then type another -- output streams per
        // command, not buffered to the end.
        await channel.SendAsync("echo first-line\n");
        await channel.WaitUntilOutputContainsAsync("first-line");
        await channel.SendAsync("echo second-line\n");
        await channel.WaitUntilOutputContainsAsync("second-line");

        await channel.SendEofAsync();
        var (outcome, _) = await runner.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TaskOutcome.Succeeded, outcome);
    }

    [Fact]
    public async Task RunAsync_EndsWhenTheShellExitsOnItsOwn()
    {
        var channel = new FakeChannel();

        // `exit` as the initial command: the shell ends itself, no operator
        // eof needed -- the channel completes through its own grammar.
        var (outcome, output) = await InteractiveShell.RunAsync("exit 3", channel, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("code 3", output);
    }

    [Fact]
    public async Task RunAsync_KillsTheShellWhenTheStreamEnds()
    {
        var channel = new FakeChannel();
        using var gone = new CancellationTokenSource();

        // A live channel ends with the stream that carries it: the shell has
        // proven itself live (it answered the initial command), then the
        // cancellation kills it whole-tree and the handler reports the close.
        var runner = InteractiveShell.RunAsync("echo alive-marker", channel, gone.Token);
        await channel.WaitUntilOutputContainsAsync("alive-marker");
        gone.Cancel();

        var (outcome, output) = await runner.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("channel closed", output);
    }

    /// <summary>
    /// A fake channel: collects the output chunks the handler streams and
    /// hands the handler input on demand, so the tests drive a live session's
    /// ordering (type, read, type again) without a wire.
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
        // effect of the command the test typed.
        public async Task WaitUntilOutputContainsAsync(string marker, TimeSpan? deadline = null)
        {
            var end = DateTimeOffset.UtcNow + (deadline ?? TimeSpan.FromSeconds(30));
            while (DateTimeOffset.UtcNow < end)
            {
                if (OutputText.Contains(marker))
                    return;
                await Task.Delay(25);
            }
        }
    }
}
