namespace Rod.Transport.Listeners.ShellCatch;

/// <summary>One decoded slice of a caught shell's output stream.</summary>
/// <param name="Sequence">
/// Monotonic per-session sequence number, starting at 1 -- the cursor the
/// output endpoint reads since, so a reconnecting console resumes exactly
/// where it stopped rather than replaying or skipping.
/// </param>
/// <param name="At">When the slice was captured.</param>
/// <param name="Text">The decoded text of the slice.</param>
public sealed record ShellOutputChunk(long Sequence, DateTimeOffset At, string Text);

/// <summary>
/// The bounded, sequence-coursed log of one caught shell's output. The
/// socket pump is the only writer; the operator console is any number of
/// readers. Readers hold a cursor (a sequence number) and pull the chunks
/// after it, then wait for the log to advance -- the wait is what turns
/// polling into a live tail without a channel per subscriber.
///
/// The log is a working buffer, not the record: it keeps the last
/// <see cref="RetainedChunks"/> slices so a console that reconnects or
/// scrolls back has recent history, and the audit trail (architecture.md
/// Sec 11) remains the durable transcript. Bound is by chunk count, not
/// bytes -- a cat of a large file flows through as many slices and the old
/// ones fall off the front, which is the correct failure for a tail view.
/// </summary>
public sealed class ShellOutputLog
{
    /// <summary>
    /// How many recent slices the log retains. Sized for a console's scroll
    /// back, not for transcript duty.
    /// </summary>
    public const int RetainedChunks = 2048;

    private readonly object _gate = new();
    private readonly Queue<ShellOutputChunk> _chunks = new();
    private long _latestSequence;
    private TaskCompletionSource _advanced = NewCompletion();

    /// <summary>The highest sequence number appended; 0 before any output.</summary>
    public long LatestSequence
    {
        get
        {
            lock (_gate)
                return _latestSequence;
        }
    }

    /// <summary>
    /// Appends a decoded slice and wakes every waiter. The sequence is
    /// assigned here, under the lock, so cursors stay gapless even with
    /// interleaved pump writes.
    /// </summary>
    public ShellOutputChunk Append(string text, DateTimeOffset at)
    {
        ShellOutputChunk chunk;
        TaskCompletionSource advanced;
        lock (_gate)
        {
            chunk = new ShellOutputChunk(++_latestSequence, at, text);
            _chunks.Enqueue(chunk);
            while (_chunks.Count > RetainedChunks)
                _chunks.Dequeue();
            advanced = _advanced;
            _advanced = NewCompletion();
        }

        advanced.TrySetResult();
        return chunk;
    }

    /// <summary>
    /// The retained slices with sequence numbers after
    /// <paramref name="cursor"/>, oldest first -- the catch-up half of a
    /// reconnecting console. Chunks aged out of the bound are skipped
    /// silently: the reader's cursor simply advances past them.
    /// </summary>
    public IReadOnlyList<ShellOutputChunk> ReadSince(long cursor)
    {
        lock (_gate)
            return _chunks.Where(c => c.Sequence > cursor).ToArray();
    }

    /// <summary>
    /// Resolves when a slice beyond <paramref name="cursor"/> has been
    /// appended (the live-tail half of the console's read loop). Returns the
    /// sequence to read since; a cancelled wait is how a disconnected
    /// console's reader is released.
    /// </summary>
    public async Task<long> WaitForAdvanceAsync(long cursor, CancellationToken cancellationToken)
    {
        while (true)
        {
            TaskCompletionSource advanced;
            lock (_gate)
            {
                if (_latestSequence > cursor)
                    return _latestSequence;
                advanced = _advanced;
            }

            await Task.WhenAny(advanced.Task, Task.Delay(Timeout.Infinite, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
