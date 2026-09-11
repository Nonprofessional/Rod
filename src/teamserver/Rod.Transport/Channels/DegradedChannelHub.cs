using System.Collections.Concurrent;
using Google.Protobuf;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;
using Rod.V1;
// The domain entity shares its name with the BCL Task; this file uses the
// Tasks namespace for the service types but never the entity by name, so pin
// Task to the BCL type the signatures need.
using Task = System.Threading.Tasks.Task;
using TaskOutcome = Rod.CoreState.Tasks.TaskOutcome;

namespace Rod.Transport.Channels;

// The store-and-forward half of the degraded channel discipline
// (architecture.md Sec 10.3): the parking queue operator input waits in when
// the implant's carrier is a poll shape, the drain the envelope and
// message-pipe check-ins deliver it through, and the idle deadline that
// closes a channel the implant stopped collecting. The live-sink half stays
// in LiveChannelHub; this hub exists because a poll carrier has no sink to
// attach -- the queue IS the sink, and the check-in cycle is its pump.

/// <summary>
/// Parks operator input for dispatched channel tasks against implants whose
/// active session advertised the degraded discipline, delivers it as
/// ChannelInput frames on their poll check-ins, and times out channels the
/// implant stopped collecting. The opt-in is the session's advertisement
/// (the handshake capability <see cref="Capability"/>), so a poll build that
/// never opted in keeps the live-stream-only behavior: nothing parks, and
/// the input route's refusal stands.
/// </summary>
internal sealed class DegradedChannelHub
{
    /// <summary>
    /// The handshake capability an opted-in implant advertises: "this
    /// artifact accepts channel traffic over its poll check-ins." The wire
    /// contract's name (extending/implants.md); the implant bakes it from
    /// its profile's degraded-channels flag.
    /// </summary>
    public const string Capability = "channels.poll";

    // The parking bounds, per task: operator input arrives at typing speed,
    // so a queue past this depth means the implant stopped collecting -- the
    // same judgment BeaconLiveChannel's bound makes on the live path, with
    // the same answer (a refusal the operator reads, not unbounded memory).
    private const int MaxQueuedUnits = 128;
    private const int MaxQueuedBytes = 256 * 1024;

    // How long a parked channel tolerates no collection before the server
    // closes it with a timeout result: generous against a slow poll cadence,
    // finite against an implant that never comes back. Internal for the
    // tests, which run the deadline on a toy clock.
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

    private readonly ISessionRegistry _sessions;
    private readonly TaskService _tasks;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<ImplantId, ConcurrentDictionary<Guid, ParkedTask>> _parked = new();

    internal TimeSpan IdleTimeout { get; set; } = DefaultIdleTimeout;

    public DegradedChannelHub(ISessionRegistry sessions, TaskService tasks, TimeProvider clock)
    {
        _sessions = sessions;
        _tasks = tasks;
        _clock = clock;
    }

    /// <summary>
    /// Whether the implant's active session advertised the degraded
    /// discipline -- the admission every park and drain shares.
    /// </summary>
    public async Task<bool> AdvertisedAsync(ImplantId implant, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetActiveAsync(implant, cancellationToken);
        return session?.Capabilities.Contains(Capability) == true;
    }

    /// <summary>
    /// Parks one input unit for the task. False when the implant has not
    /// opted in, the task's queue is full (the operator's refill signal, not
    /// a server error), or the channel timed out and was closed.
    /// </summary>
    public async Task<bool> TryEnqueueAsync(
        ImplantId implant,
        TaskId task,
        byte[] data,
        bool eof,
        CancellationToken cancellationToken)
    {
        if (!await AdvertisedAsync(implant, cancellationToken))
            return false;
        // The sweep first: a channel it just closed must not admit a fresh
        // park into the queue its removal emptied.
        var closed = await SweepIdleAsync(cancellationToken);
        if (closed.Contains(task.Value))
            return false;

        var tasks = _parked.GetOrAdd(implant, _ => new ConcurrentDictionary<Guid, ParkedTask>());
        var parked = tasks.GetOrAdd(task.Value, _ => new ParkedTask(_clock.GetUtcNow()));
        lock (parked)
        {
            if (parked.TimedOut)
                return false;
            if (parked.Units >= MaxQueuedUnits || parked.Bytes + data.Length > MaxQueuedBytes)
                return false;
            parked.Enqueue(data, eof, _clock.GetUtcNow());
        }
        return true;
    }

    /// <summary>
    /// The implant's parked input as ChannelInput frames, in arrival order,
    /// while <paramref name="maxBytes"/> lasts -- what a poll check-in's
    /// response carries after its tasking. Collecting refreshes the
    /// channels' idle clocks; a task whose eof has been collected and whose
    /// queue is empty leaves the park (the channel's close rides the
    /// implant's own final result).
    /// </summary>
    public IReadOnlyList<Frame> Drain(ImplantId implant, int maxBytes)
    {
        if (!_parked.TryGetValue(implant, out var tasks) || tasks.IsEmpty)
            return Array.Empty<Frame>();

        var now = _clock.GetUtcNow();
        var outbound = new List<Frame>();
        var budget = maxBytes;
        foreach (var (taskId, parked) in tasks)
        {
            lock (parked)
            {
                parked.LastCollectedAt = now;
                while (budget > 0 && parked.TryDequeue(out var unit))
                {
                    var input = new ChannelInput
                    {
                        TaskId = taskId.ToString(),
                        Eof = unit.Eof,
                    };
                    if (unit.Data.Length > 0)
                        input.Data = ByteString.CopyFrom(unit.Data);
                    var frame = new Frame
                    {
                        Payload = ByteString.CopyFrom(input.ToByteArray()),
                        Kind = FrameKind.ChannelInput,
                    };
                    var wireSize = input.CalculateSize() + 8;
                    budget -= wireSize;
                    outbound.Add(frame);
                }
                if (parked.EofDelivered && parked.IsEmpty)
                    tasks.TryRemove(taskId, out _);
            }
        }
        if (tasks.IsEmpty)
            _parked.TryRemove(implant, out _);
        return outbound;
    }

    /// <summary>
    /// Closes parked channels idle past the deadline with a timeout result,
    /// so the operator reads the channel's end instead of watching a
    /// Dispatched task forever, and returns the closed tasks' ids -- the
    /// park path's refuse-set. Runs opportunistically on the park and drain
    /// paths, the same lazy-sweep economy the session registry's caller
    /// applies.
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>> SweepIdleAsync(CancellationToken cancellationToken)
    {
        var closed = new List<Guid>();
        var cutoff = _clock.GetUtcNow() - IdleTimeout;
        foreach (var (implant, tasks) in _parked)
        {
            foreach (var (taskId, parked) in tasks)
            {
                bool timedOut;
                lock (parked)
                {
                    timedOut = parked.LastCollectedAt < cutoff && parked.LastActivityAt < cutoff;
                    parked.TimedOut = timedOut || parked.TimedOut;
                }
                if (!timedOut)
                    continue;
                if (tasks.TryRemove(taskId, out _))
                {
                    closed.Add(taskId);
                    try
                    {
                        await _tasks.RecordResultAsync(
                            new TaskId(taskId),
                            "channel timed out: the implant stopped collecting its poll check-ins",
                            TaskOutcome.Failed,
                            cancellationToken);
                    }
                    catch (InvalidOperationException)
                    {
                        // Already completed (the implant's own final result
                        // raced the sweep): the channel is closed either way.
                    }
                }
            }
            if (tasks.IsEmpty)
                _parked.TryRemove(implant, out _);
        }
        return closed;
    }

    // One task's parked queue. The lock is per task: typing-speed posts on
    // one channel must not serialize against another's.
    private sealed class ParkedTask(DateTimeOffset now)
    {
        private readonly Queue<(byte[] Data, bool Eof)> _units = new();

        public int Units { get; private set; }

        public int Bytes { get; private set; }

        public DateTimeOffset LastActivityAt { get; private set; } = now;

        public DateTimeOffset LastCollectedAt { get; set; } = now;

        public bool EofDelivered { get; private set; }

        public bool TimedOut { get; set; }

        public void Enqueue(byte[] data, bool eof, DateTimeOffset at)
        {
            _units.Enqueue((data, eof));
            Units++;
            Bytes += data.Length;
            LastActivityAt = at;
            if (eof)
                EofDelivered = true;
        }

        public bool TryDequeue(out (byte[] Data, bool Eof) unit)
        {
            if (!_units.TryDequeue(out unit))
                return false;
            Units--;
            Bytes -= unit.Data.Length;
            return true;
        }

        public bool IsEmpty => _units.Count == 0;
    }
}
