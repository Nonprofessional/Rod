using System.Collections.Concurrent;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Tasks;

/// <summary>
/// The default <see cref="ITaskDispatchWake"/>: one
/// <see cref="SemaphoreSlim"/> per implant, so permits accumulate while the
/// implant is offline and a release racing a wait is never lost. Entries live
/// for the process -- the set is bounded by the enrolled fleet, the same
/// bound the session registry carries, so there is no reclamation path to get
/// wrong.
/// </summary>
public sealed class InMemoryTaskDispatchWake : ITaskDispatchWake
{
    private readonly ConcurrentDictionary<ImplantId, SemaphoreSlim> _queues = new();

    public System.Threading.Tasks.Task WaitAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default)
        => Queue(implant).WaitAsync(cancellationToken);

    public void Release(ImplantId implant) => Queue(implant).Release();

    private SemaphoreSlim Queue(ImplantId implant)
        => _queues.GetOrAdd(implant, static _ => new SemaphoreSlim(initialCount: 0));
}
