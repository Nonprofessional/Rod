using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
// The domain entity shares its name with System.Threading.Tasks.Task. Pin it
// here so the port's own Task type wins; the BCL type is reached by its full
// name where the methods return it.
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.CoreState.Tasks;

/// <summary>
/// Persistence port for <see cref="Task"/> aggregates. The core-state layer
/// (roadmap M2.1) shapes this as a first-class task queue and history: per-implant
/// FIFO dequeue for dispatch, plus implant- and engagement-scoped history. Every
/// query is rooted in an implant or engagement (which themselves belong to one
/// engagement), so cross-engagement access stays impossible by construction
/// (architecture.md Sec 3).
///
/// The walking skeleton ships an in-memory implementation; the port keeps callers
/// agnostic to that.
/// </summary>
public interface ITaskRepository
{
    /// <summary>
    /// Stores <paramref name="task"/>, inserting or replacing by id. Re-saving a
    /// dispatched/completed task keeps it in history with its updated status.
    /// </summary>
    System.Threading.Tasks.Task SaveAsync(Task task, CancellationToken cancellationToken = default);

    /// <summary>The task, or null when unknown.</summary>
    System.Threading.Tasks.Task<Task?> FindAsync(TaskId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// All tasks directed at an implant, oldest first -- the implant's task
    /// history across queued, dispatched, and completed states.
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyList<Task>> ListByImplantAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// All tasks in an engagement, oldest first -- the engagement's task history.
    /// Scoped by engagement so cross-engagement access never reaches this with
    /// another engagement's id (roadmap M2.1).
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyList<Task>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The next not-yet-dispatched task for an implant, oldest first (FIFO by
    /// enqueue time), or null when none is queued. A peek: the caller (task
    /// issuance gating, tests) reads without changing state.
    /// </summary>
    System.Threading.Tasks.Task<Task?> NextPendingAsync(ImplantId implant, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims the next queued task for an implant: marks it
    /// Dispatched at <paramref name="at"/> and persists the transition before
    /// returning it, or returns null when nothing is queued. Concurrent claims
    /// for the same implant never hand out the same task -- the beacon writer
    /// drains through this, so a reconnect overlap cannot double-dispatch
    /// (architecture.md Sec 10.3).
    /// </summary>
    System.Threading.Tasks.Task<Task?> ClaimNextPendingAsync(
        ImplantId implant,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);
}
