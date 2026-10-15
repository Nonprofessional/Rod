namespace Rod.Operators.Auth;

/// <summary>
/// Login throttle (architecture.md Sec 9). With no per-engagement RBAC, an
/// operator account is the only boundary between an attacker and the whole
/// teamserver, so the login endpoint slows brute force instead of letting it
/// run unbounded -- on both axes a spray uses:
/// <list type="bullet">
/// <item>per handle: five failures within the window put the handle into a
/// cooldown, so one account cannot be ground on unbounded;</item>
/// <item>per source address: a wider budget (a shared NAT must not lock out
/// its legitimate users), so spraying many handles from one address still
/// engages a cooldown -- the per-handle axis alone never noticed that
/// shape.</item>
/// </list>
/// A successful login resets its handle's counter (the address budget
/// stands: it only ever counts failures). In-memory by design: the cooldown
/// is a best-effort control and a restart clearing it is acceptable; the
/// credential store itself is the durable boundary.
/// </summary>
public sealed class LoginThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int MaxHandleFailures = 5;
    private const int MaxAddressFailures = 20;

    private readonly Dictionary<string, Attempts> _attempts = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly TimeProvider _clock;

    public LoginThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// True when a login attempt for <paramref name="handle"/> from
    /// <paramref name="address"/> may proceed. A handle or an address in
    /// cooldown after repeated failures is refused without touching the
    /// credential store.
    /// </summary>
    public bool IsAllowed(string handle, string? address = null)
    {
        lock (_lock)
        {
            if (!WithinBudget(HandleKey(handle), MaxHandleFailures))
                return false;
            if (address is not null && !WithinBudget(AddressKey(address), MaxAddressFailures))
                return false;
            return true;
        }
    }

    /// <summary>Records a failed attempt on both axes, entering cooldown past either cap.</summary>
    public void RecordFailure(string handle, string? address = null)
    {
        lock (_lock)
        {
            Record(HandleKey(handle));
            if (address is not null)
                Record(AddressKey(address));
        }
    }

    /// <summary>Clears the handle's counter after a successful login.</summary>
    public void Reset(string handle)
    {
        lock (_lock)
        {
            _attempts.Remove(HandleKey(handle));
        }
    }

    private bool WithinBudget(string key, int cap)
    {
        if (!_attempts.TryGetValue(key, out var attempts))
            return true;

        // A stale window starts fresh; within the window the cooldown
        // engages once the failure cap is reached.
        if (_clock.GetUtcNow() - attempts.FirstAt >= Window)
            return true;
        return attempts.Count < cap;
    }

    private void Record(string key)
    {
        var now = _clock.GetUtcNow();
        if (!_attempts.TryGetValue(key, out var attempts))
        {
            _attempts[key] = new Attempts(1, now);
            return;
        }

        // A stale window starts fresh; within the window, count up.
        if (now - attempts.FirstAt >= Window)
            _attempts[key] = new Attempts(1, now);
        else
            _attempts[key] = new Attempts(attempts.Count + 1, attempts.FirstAt);
    }

    // The two axes live in one dictionary under distinct prefixes: a handle
    // can never collide with an address string.
    private static string HandleKey(string handle) => "h:" + handle;
    private static string AddressKey(string address) => "a:" + address;

    private sealed record Attempts(int Count, DateTimeOffset FirstAt);
}
