using System.Collections.Concurrent;
using Rod.CoreState;
using Rod.CoreState.Tasks;

namespace Rod.Transport.Channels;

/// <summary>
/// The per-implant registry of live channel sinks (architecture.md Sec 10.3,
/// the streaming task shape): the rendezvous between the operator input route
/// -- which arrives over HTTP, outside the beacon stream -- and the beacon
/// stream's dispatch writer, which is the stream's sole WriteAsync caller.
///
/// A session-scoped channel lives on the beacon stream that carried its
/// TaskRequest, so the sink a stream registers is the stream's own input
/// queue: the route enqueues onto it and releases the per-implant dispatch
/// wake, and the writer drains it downstream as ChannelInput frames (the same
/// claim-first/wake-on-release shape queued tasking uses). A stream attaches
/// on handshake and detaches when it ends; a reconnect's attach replaces a
/// still-registered prior sink, and the prior stream's detach then leaves the
/// newer registration alone. An enqueue for an implant with no live sink fails
/// -- there is no channel to carry it -- which the input route reports as a
/// conflict rather than queueing input no stream will ever drain.
/// </summary>
internal sealed class LiveChannelHub
{
    private readonly ConcurrentDictionary<ImplantId, ILiveChannelSink> _byImplant = new();

    /// <summary>
    /// Registers <paramref name="sink"/> as the implant's live sink, replacing
    /// any registration a prior stream left. The returned disposable detaches
    /// it, but only while it is still the registered sink -- a reconnect that
    /// already replaced it owns the slot.
    /// </summary>
    public IDisposable Attach(ImplantId implant, ILiveChannelSink sink)
    {
        _byImplant[implant] = sink;
        return new Attachment(this, implant, sink);
    }

    /// <summary>
    /// Enqueues one unit of operator input for the implant's live channel.
    /// False when no sink is registered (no live stream) or the stream's
    /// buffer is full (a stream that cannot keep up must not pin operator
    /// memory) -- both are the route's conflict to report.
    /// </summary>
    public bool TryEnqueue(ImplantId implant, Guid taskId, ReadOnlyMemory<byte> data, bool eof)
        => _byImplant.TryGetValue(implant, out var sink)
           && sink.TryEnqueue(taskId, data, eof);

    private sealed class Attachment(LiveChannelHub hub, ImplantId implant, ILiveChannelSink sink) : IDisposable
    {
        public void Dispose()
        {
            // Remove only this attachment's registration: a reconnect whose
            // attach replaced this sink must keep its own.
            _ = hub._byImplant.TryRemove(new KeyValuePair<ImplantId, ILiveChannelSink>(implant, sink));
        }
    }
}

/// <summary>
/// The input sink one beacon stream registers for its implant: a bounded
/// queue the operator route produces and the stream's dispatch writer drains.
/// The queue is session-scoped -- it dies with the stream, so input for a
/// dead channel never queues indefinitely.
/// </summary>
internal interface ILiveChannelSink
{
    /// <summary>
    /// Accepts one unit of operator input for the channel named by
    /// <paramref name="taskId"/>. False when the queue is at capacity.
    /// </summary>
    bool TryEnqueue(Guid taskId, ReadOnlyMemory<byte> data, bool eof);
}

/// <summary>
/// One queued unit of operator input: the channel's task, the bytes (empty
/// when the unit is an eof alone), and whether the operator closed the
/// channel's stdin.
/// </summary>
internal readonly record struct ChannelInputUnit(Guid TaskId, byte[] Data, bool Eof);

/// <summary>
/// The beacon stream's <see cref="ILiveChannelSink"/>: a bounded queue plus
/// the per-implant dispatch wake. An accepted enqueue releases the wake so the
/// writer pushes the input downstream immediately -- the same hint-not-ledger
/// contract queued tasking rides (architecture.md Sec 10.3).
/// </summary>
internal sealed class BeaconChannelSink(ITaskDispatchWake wake, ImplantId implant) : ILiveChannelSink
{
    // The queue depth ceiling: a stream that cannot drain fast enough must
    // not let operator posts pin memory. Inputs are keystrokes and pastes --
    // a bounded queue of them is a stuck stream, which the route reports.
    private const int MaxQueuedInputs = 256;

    private readonly ConcurrentQueue<ChannelInputUnit> _pending = new();

    public bool TryEnqueue(Guid taskId, ReadOnlyMemory<byte> data, bool eof)
    {
        if (_pending.Count >= MaxQueuedInputs)
            return false;

        _pending.Enqueue(new ChannelInputUnit(taskId, data.ToArray(), eof));
        wake.Release(implant);
        return true;
    }

    /// <summary>Drains the queued units in arrival order. The writer is the sole drainer.</summary>
    public bool TryDequeue(out ChannelInputUnit unit) => _pending.TryDequeue(out unit);
}
