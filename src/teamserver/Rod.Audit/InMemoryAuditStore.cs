using System.Collections.Concurrent;
using System.Threading;

namespace Rod.Audit;

/// <summary>
/// In-memory <see cref="IAuditStore"/> by default -- the durable Postgres
/// pair replaces it when configured. Events live in a process-local append-only store keyed by
/// <see cref="AuditEvent.EventId"/>; engagement- and task-scoped queries filter
/// that store. Append-only is honored by contract: the only mutation is
/// <see cref="AppendAsync"/>, and nothing here removes or rewrites an event --
/// appending the same <see cref="AuditEvent.EventId"/> twice throws. State is
/// lost on restart; the port keeps callers agnostic to that.
///
/// Hash-chained per engagement (storage &amp; audit layer): each
/// appended event is stamped with the hash of the previous event in its
/// engagement (the genesis all-zero hash for the first) and a hash over itself,
/// via <see cref="AuditChain"/>. Tampering with a stored event therefore breaks
/// the chain at the next link. Each engagement has its own independent chain, so
/// cross-engagement events never share a hash head -- mirroring the per-engagement
/// trail (architecture.md Sec 3, Sec 11). The append and the head advance are
/// made atomic by a lock so concurrent appends serialize correctly within an
/// engagement (the only adapter needing one, matching the stager-token redeem).
/// </summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly ConcurrentDictionary<Guid, AuditEvent> _events = new();
    private readonly ConcurrentDictionary<Guid, string> _heads = new();

    // Append + head-advance must be atomic: two concurrent appends to one
    // engagement would otherwise both read the same head and produce events that
    // claim the same predecessor. System.Threading.Lock is the dedicated type the
    // other adapters use (net10.0).
    private readonly Lock _appendLock = new();

    public Task AppendAsync(AuditEvent @event, CancellationToken cancellationToken = default)
    {
        // The caller passes a Fact (hash fields empty); this stamps it onto the
        // engagement's chain. Duplicate EventIds are rejected -- append-only means
        // an event, once written, is never overwritten.
        lock (_appendLock)
        {
            if (_events.ContainsKey(@event.EventId))
                throw new InvalidOperationException(
                    $"Audit event {@event.EventId} is already appended; the audit trail is append-only.");

            var previousHash = _heads.GetValueOrDefault(@event.EngagementId, AuditChain.GenesisHash);
            var chained = AuditChain.Chain(@event, previousHash);
            _events[@event.EventId] = chained;
            _heads[@event.EngagementId] = chained.Hash;
        }

        return Task.CompletedTask;
    }

    public Task<AuditEvent?> FindAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        _events.TryGetValue(eventId, out var found);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<AuditEvent>> ForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        // The listing reads snapshot under the append lock: an unlocked
        // enumeration racing an append can observe a newer event while
        // missing its predecessor, and a chain verification over that torn
        // snapshot reports a break that is not there. The lock the appends
        // already share makes the snapshot whole.
        lock (_appendLock)
        {
            var matches = _events.Values
                .Where(e => e.TaskId == taskId)
                .OrderBy(e => e.At)
                .ToArray();
            return Task.FromResult<IReadOnlyList<AuditEvent>>(matches);
        }
    }

    public Task<IReadOnlyList<AuditEvent>> ForImplantAsync(Guid implantId, CancellationToken cancellationToken = default)
    {
        // Same locked snapshot discipline as ForTaskAsync.
        lock (_appendLock)
        {
            var matches = _events.Values
                .Where(e => e.ImplantId == implantId)
                .OrderBy(e => e.At)
                .ToArray();
            return Task.FromResult<IReadOnlyList<AuditEvent>>(matches);
        }
    }

    public Task<IReadOnlyList<AuditEvent>> ListAsync(Guid engagementId, CancellationToken cancellationToken = default)
    {
        lock (_appendLock)
        {
            var matches = _events.Values
                .Where(e => e.EngagementId == engagementId)
                .ToArray();

            // Order by the chain itself, the way the file-backed and Postgres
            // adapters do: the hash uses millisecond precision, so two appends
            // can share an instant, and a ConcurrentDictionary enumerates in
            // no guaranteed order -- an At-sorted listing may then present
            // same-instant events out of append order, and a verification
            // walking that listing reports a break that is not there. The
            // PreviousHash links are a total append order (appends serialize
            // under this lock), so walking them from genesis restores it. A
            // walk that somehow comes up short falls back to At-order rather
            // than dropping events.
            var successorByLink = new Dictionary<string, AuditEvent>(matches.Length, StringComparer.Ordinal);
            foreach (var @event in matches)
                successorByLink[@event.PreviousHash] = @event;

            var ordered = new List<AuditEvent>(matches.Length);
            var link = AuditChain.GenesisHash;
            while (successorByLink.TryGetValue(link, out var next))
            {
                ordered.Add(next);
                link = next.Hash;
            }

            if (ordered.Count == matches.Length)
                return Task.FromResult<IReadOnlyList<AuditEvent>>(ordered);

            return Task.FromResult<IReadOnlyList<AuditEvent>>(
                matches.OrderBy(e => e.At).ThenBy(e => e.EventId).ToArray());
        }
    }

    public Task<AuditPage> ListPageAsync(
        Guid engagementId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        // The event id breaks timestamp ties so a page boundary is stable even
        // when several events share one instant.
        lock (_appendLock)
        {
            var ordered = _events.Values
                .Where(e => e.EngagementId == engagementId)
                .OrderBy(e => e.At)
                .ThenBy(e => e.EventId)
                .ToArray();
            var (items, next) = ListPageWindow.TakeNewest(
                ordered, limit, cursor, e => e.At, e => e.EventId);
            return Task.FromResult(new AuditPage(items, next));
        }
    }
}
