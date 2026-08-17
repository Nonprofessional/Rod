using Rod.CoreState.Implants;

namespace Rod.CoreState.Tasks;

/// <summary>
/// The per-implant task-queue wake (architecture.md Sec 10.3): every enqueue
/// the queue accepts -- a fresh issuance or a dispatch returned by a failed
/// write -- releases one permit for the task's implant, and the beacon
/// stream's dispatch writer parks on <see cref="WaitAsync"/> until a permit
/// arrives. A queued task is therefore pushed downstream the moment it is
/// queued, and an idle stream costs nothing -- no poll loop in the writer
/// path.
/// </summary>
/// <remarks>
/// The wake is a hint, not a ledger. A permit promises only that the queue
/// may hold work, so the writer claims first and parks only when the claim
/// comes back empty; permits released while no stream is open accumulate on
/// the implant's queue and are drained as no-op claims after the next
/// connect. The claim-first shape is what makes the wake safe: a stale or
/// missing permit can never lose a task, only cost one empty claim.
/// </remarks>
public interface ITaskDispatchWake
{
    /// <summary>
    /// Parks until the next release for <paramref name="implant"/>, or
    /// returns immediately when a permit is already held -- so a release
    /// racing the wait is never lost.
    /// </summary>
    System.Threading.Tasks.Task WaitAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases one permit for <paramref name="implant"/>. Called exactly
    /// once per accepted enqueue.
    /// </summary>
    void Release(ImplantId implant);
}
