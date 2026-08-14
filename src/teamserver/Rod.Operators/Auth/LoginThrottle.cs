namespace Rod.Operators.Auth;

/// <summary>
/// Per-handle login throttle (architecture.md Sec 9). With no per-engagement
/// RBAC, an operator account is the only boundary between an attacker and the
/// whole teamserver, so the login endpoint slows brute force instead of letting
/// it run unbounded: five failures within the window put the handle into a
/// cooldown that refuses further attempts until the window passes. A successful
/// login resets the counter. In-memory by design -- the cooldown is a
/// walking-skeleton control, and a restart clearing it is acceptable; the
/// credential store itself is the durable boundary.
/// </summary>
public sealed class LoginThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int MaxFailures = 5;

    private readonly Dictionary<string, Attempts> _byHandle = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly TimeProvider _clock;

    public LoginThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// True when a login attempt for <paramref name="handle"/> may proceed. A
    /// handle in cooldown after repeated failures is refused without touching
    /// the credential store.
    /// </summary>
    public bool IsAllowed(string handle)
    {
        lock (_lock)
        {
            if (!_byHandle.TryGetValue(handle, out var attempts))
                return true;

            // A stale window starts fresh; within the window the cooldown
            // engages once the failure cap is reached.
            if (_clock.GetUtcNow() - attempts.FirstAt >= Window)
                return true;
            return attempts.Count < MaxFailures;
        }
    }

    /// <summary>Records a failed attempt, entering cooldown past the failure cap.</summary>
    public void RecordFailure(string handle)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (!_byHandle.TryGetValue(handle, out var attempts))
            {
                _byHandle[handle] = new Attempts(1, now);
                return;
            }

            // A stale window starts fresh; within the window, count up.
            if (now - attempts.FirstAt >= Window)
                _byHandle[handle] = new Attempts(1, now);
            else
                _byHandle[handle] = new Attempts(attempts.Count + 1, attempts.FirstAt);
        }
    }

    /// <summary>Clears the counter after a successful login.</summary>
    public void Reset(string handle)
    {
        lock (_lock)
        {
            _byHandle.Remove(handle);
        }
    }

    private sealed record Attempts(int Count, DateTimeOffset FirstAt);
}
