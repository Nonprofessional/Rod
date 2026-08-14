using System.Collections.Concurrent;
using System.Threading;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
// The domain entity shares its name with System.Threading.Tasks.Task. Pin it
// here so the repository's own Task type wins; the BCL type is reached by its
// full name where the methods below return it.
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.CoreState.Tasks;

/// <summary>
/// In-memory <see cref="ITaskRepository"/> for the walking skeleton (roadmap M2
/// -- no Postgres yet). Tasks live in a process-local map keyed by task id;
/// per-implant dispatch drains the queued tasks in FIFO enqueue order, and the
/// same map backs the implant- and engagement-scoped history. Implant- and
/// engagement-scoped queries filter that map, so a caller scoped to one
/// engagement never sees another's tasks. State is lost on restart; the port
/// keeps callers agnostic to that.
/// </summary>
public sealed class InMemoryTaskRepository : ITaskRepository
{
    private readonly ConcurrentDictionary<TaskId, Task> _tasks = new();

    // A single global enqueue sequence so history ordering is deterministic
    // regardless of which implant or engagement a task belongs to. Restricting
    // this order to one implant is that implant's enqueue order, so per-implant
    // dispatch stays FIFO; an explicit per-implant counter would tie across
    // implants and leave history order nondeterministic.
    private long _nextSequence = 1;
    private readonly ConcurrentDictionary<TaskId, long> _order = new();

    public System.Threading.Tasks.Task SaveAsync(Task task, CancellationToken cancellationToken = default)
    {
        // Record the enqueue order only the first time a task is seen: re-saves
        // (dispatch, completion) must not move a task to the back of its queue.
        if (_tasks.TryAdd(task.Id, task))
        {
            _order[task.Id] = Interlocked.Increment(ref _nextSequence);
        }
        else
        {
            _tasks[task.Id] = task;
        }
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
            .OrderBy(t => _order.GetValueOrDefault(t.Id))
            .ToArray();
        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<Task>>(matches);
    }

    public System.Threading.Tasks.Task<IReadOnlyList<Task>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        var matches = _tasks.Values
            .Where(t => t.EngagementId == engagement)
            .OrderBy(t => _order.GetValueOrDefault(t.Id))
            .ToArray();
        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<Task>>(matches);
    }

    public System.Threading.Tasks.Task<Task?> NextPendingAsync(ImplantId implant, CancellationToken cancellationToken = default)
    {
        // Oldest still-queued task for the implant by global enqueue order; the
        // beacon drains these one at a time on each check-in.
        var next = _tasks.Values
            .Where(t => t.ImplantId == implant && t.Status == TaskStatus.Queued)
            .OrderBy(t => _order.GetValueOrDefault(t.Id))
            .FirstOrDefault();
        return System.Threading.Tasks.Task.FromResult(next);
    }

    public System.Threading.Tasks.Task<Task?> ClaimNextPendingAsync(
        ImplantId implant,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        // The peek-and-mark transition runs under one lock so two concurrent
        // claims for the same implant cannot observe the same Queued task; the
        // second claim sees the first's Dispatched state and moves on.
        lock (_claimLock)
        {
            var next = _tasks.Values
                .Where(t => t.ImplantId == implant && t.Status == TaskStatus.Queued)
                .OrderBy(t => _order.GetValueOrDefault(t.Id))
                .FirstOrDefault();
            if (next is null)
                return System.Threading.Tasks.Task.FromResult<Task?>(null);

            next.MarkDispatched(at);
            _tasks[next.Id] = next;
            return System.Threading.Tasks.Task.FromResult<Task?>(next);
        }
    }

    private readonly Lock _claimLock = new();
}
