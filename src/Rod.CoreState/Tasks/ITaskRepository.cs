using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
// The domain entity shares its name with System.Threading.Tasks.Task. Pin it
// here so the port's own Task type wins; the BCL type is reached by its full
// name where the methods return it.
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.CoreState.Tasks;

/// <summary>
/// Persistence port for <see cref="Task"/> aggregates (roadmap M1.4). The
/// walking skeleton ships an in-memory implementation; the port keeps callers
/// agnostic to that. Tasks are engagement- and implant-scoped: every query is
/// rooted in an implant (which itself belongs to one engagement), so
/// cross-engagement access stays impossible by construction (architecture.md
/// Sec 3).
///
/// The port exposes only what the tasking slice needs: store a task, look it up,
/// list an implant's tasks, and pull the next queued task to dispatch. A
/// first-class task queue / history store arrives with the core-state layer
/// (roadmap M2.1).
/// </summary>
public interface ITaskRepository
{
    /// <summary>Stores <paramref name="task"/>, inserting or replacing by id.</summary>
    System.Threading.Tasks.Task SaveAsync(Task task, CancellationToken cancellationToken = default);

    /// <summary>The task, or null when unknown.</summary>
    System.Threading.Tasks.Task<Task?> FindAsync(TaskId id, CancellationToken cancellationToken = default);

    /// <summary>All tasks directed at an implant, oldest first.</summary>
    System.Threading.Tasks.Task<IReadOnlyList<Task>> ListByImplantAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The next not-yet-dispatched task for an implant, oldest first, or null
    /// when none is queued. What the beacon stream drains on each check-in.
    /// </summary>
    System.Threading.Tasks.Task<Task?> NextPendingAsync(ImplantId implant, CancellationToken cancellationToken = default);
}
