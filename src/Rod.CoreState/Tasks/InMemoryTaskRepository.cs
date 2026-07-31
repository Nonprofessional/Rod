using System.Collections.Concurrent;
using Rod.CoreState.Implants;
// The domain entity shares its name with System.Threading.Tasks.Task. Pin it
// here so the repository's own Task type wins; the BCL type is reached by its
// full name where the methods below return it.
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.CoreState.Tasks;

/// <summary>
/// In-memory <see cref="ITaskRepository"/> for the walking skeleton (roadmap M1
/// -- no Postgres yet). Tasks live in a process-local map keyed by task id;
/// implant-scoped queries filter that map by implant, so a caller scoped to one
/// engagement never sees another's tasks. State is lost on restart; the port
/// keeps callers agnostic to that.
/// </summary>
public sealed class InMemoryTaskRepository : ITaskRepository
{
    private readonly ConcurrentDictionary<TaskId, Task> _tasks = new();

    public System.Threading.Tasks.Task SaveAsync(Task task, CancellationToken cancellationToken = default)
    {
        _tasks[task.Id] = task;
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public System.Threading.Tasks.Task<Task?> FindAsync(TaskId id, CancellationToken cancellationToken = default)
        => System.Threading.Tasks.Task.FromResult(_tasks.TryGetValue(id, out var task) ? task : null);

    public System.Threading.Tasks.Task<IReadOnlyList<Task>> ListByImplantAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default)
    {
        var matches = _tasks.Values
            .Where(t => t.ImplantId == implant)
            .OrderBy(t => t.CreatedAt)
            .ToArray();
        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<Task>>(matches);
    }

    public System.Threading.Tasks.Task<Task?> NextPendingAsync(ImplantId implant, CancellationToken cancellationToken = default)
    {
        // Oldest still-queued task for the implant; the beacon drains these one
        // at a time on each check-in.
        var next = _tasks.Values
            .Where(t => t.ImplantId == implant && t.Status == TaskStatus.Queued)
            .MinBy(t => t.CreatedAt);
        return System.Threading.Tasks.Task.FromResult(next);
    }
}
