using System.Collections.Concurrent;

namespace Rod.Audit;

/// <summary>
/// In-memory <see cref="IAuditStore"/> for the walking skeleton (roadmap M1 --
/// no Postgres yet). Events live in a process-local append-only list; engagement-
/// and task-scoped queries filter that list. Append-only is honored by contract:
/// the only mutation is <see cref="AppendAsync"/>, and nothing here removes or
/// rewrites an event. State is lost on restart; the port keeps callers agnostic
/// to that.
///
/// Not hash-chained yet: the M2.3 store extends this in place, threading each
/// event's hash off the previous so tampering breaks the chain. This adapter
/// preserves the ordering and the append-only contract that the chaining will
/// lean on.
/// </summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly ConcurrentBag<AuditEvent> _events = new();

    public Task AppendAsync(AuditEvent @event, CancellationToken cancellationToken = default)
    {
        _events.Add(@event);
        return Task.CompletedTask;
    }

    public Task<AuditEvent?> FindAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var found = _events.FirstOrDefault(e => e.EventId == eventId);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<AuditEvent>> ForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var matches = _events
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => e.At)
            .ToArray();
        return Task.FromResult<IReadOnlyList<AuditEvent>>(matches);
    }

    public Task<IReadOnlyList<AuditEvent>> ListAsync(Guid engagementId, CancellationToken cancellationToken = default)
    {
        var matches = _events
            .Where(e => e.EngagementId == engagementId)
            .OrderBy(e => e.At)
            .ToArray();
        return Task.FromResult<IReadOnlyList<AuditEvent>>(matches);
    }
}
