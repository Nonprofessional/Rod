using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
// The domain entity shares its name with System.Threading.Tasks.Task. Pin it
// here so the port's own Task type wins; the BCL type is reached by its full
// name where the methods return it.
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.CoreState.Tasks;

/// <summary>
/// Persistence port for <see cref="Task"/> aggregates. The core-state layer
/// shapes this as a first-class task queue and history: per-implant
/// FIFO dequeue for dispatch, plus implant- and engagement-scoped history. Every
/// query is rooted in an implant or engagement (which themselves belong to one
/// engagement), so cross-engagement access stays impossible by construction
/// (architecture.md Sec 3).
///
/// The default is an in-memory implementation; the port keeps callers
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
    /// another engagement's id.
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyList<Task>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the engagement's task history (architecture.md Sec 10.3): the
    /// newest <paramref name="limit"/> tasks, or the next older page when
    /// <paramref name="cursor"/> carries the previous page's
    /// <see cref="TaskPage.NextCursor"/>. A long engagement no longer grows the
    /// listing endpoint without bound -- the operator UI walks pages.
    /// </summary>
    System.Threading.Tasks.Task<TaskPage> ListByEngagementPageAsync(
        EngagementId engagement,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of an implant's task history, newest window first, with the same
    /// cursor semantics as <see cref="ListByEngagementPageAsync"/>.
    /// </summary>
    System.Threading.Tasks.Task<TaskPage> ListByImplantPageAsync(
        ImplantId implant,
        int limit,
        string? cursor,
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

    /// <summary>
    /// The fronting claim (architecture.md Sec 5.2, the Pivot class):
    /// atomically claims the oldest queued task across a set of implant
    /// targets -- a fronting parent and the Pivot children its stream
    /// executes for -- with the same claim-once guarantee
    /// <see cref="ClaimNextPendingAsync"/> gives one implant. FIFO across the
    /// whole set by enqueue order, so a task queued for the parent and one
    /// queued for a fronted child dispatch in the order they were issued.
    /// Null when no target holds a queued task.
    /// </summary>
    System.Threading.Tasks.Task<Task?> ClaimNextPendingForAsync(
        IReadOnlyCollection<ImplantId> implants,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically reserves and persists the next per-implant replay nonce
    /// (architecture.md Sec 9 -- tasking replay nonces): returns 1 the first
    /// time and monotonically increases after, so a negotiating implant never
    /// sees the same nonce twice and a captured frame replayed after a
    /// reconnect still falls at or below the floor it already accepted. The
    /// floor lives behind the repository so a durable store keeps it across a
    /// restart: the in-memory adapter's counter is per-process (which the
    /// signing posture tolerates -- the task queue itself is equally
    /// per-process there), while the durable adapter persists the count.
    /// </summary>
    System.Threading.Tasks.Task<ulong> NextNonceAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default);
}
