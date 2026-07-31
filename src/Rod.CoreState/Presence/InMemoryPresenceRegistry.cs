using System.Collections.Concurrent;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Presence;

/// <summary>
/// In-memory <see cref="IPresenceRegistry"/> for the walking skeleton
/// (roadmap M1 -- no Postgres yet). Online implants live in a process-local map
/// keyed by implant id; engagement-scoped queries filter that map by engagement
/// so a caller scoped to one engagement never sees another's presence. State is
/// lost on restart; the port keeps callers agnostic to that.
/// </summary>
public sealed class InMemoryPresenceRegistry : IPresenceRegistry
{
    private readonly ConcurrentDictionary<ImplantId, PresenceRecord> _online = new();

    public Task SetOnlineAsync(
        Implant implant,
        IReadOnlyCollection<string> capabilities,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        // OnlineAt is preserved across reconnects so a flap does not reset the
        // session clock; LastSeenAt advances with each handshake/keepalive.
        var existing = _online.TryGetValue(implant.Id, out var prior) ? prior : null;
        var record = new PresenceRecord(
            implant.Id,
            implant.EngagementId,
            capabilities.ToArray(),
            OnlineAt: existing?.OnlineAt ?? at,
            LastSeenAt: at);
        _online[implant.Id] = record;
        return Task.CompletedTask;
    }

    public Task SetOfflineAsync(ImplantId implant, CancellationToken cancellationToken = default)
    {
        _online.TryRemove(implant, out _);
        return Task.CompletedTask;
    }

    public Task<PresenceRecord?> FindAsync(ImplantId implant, CancellationToken cancellationToken = default)
        => Task.FromResult(_online.TryGetValue(implant, out var record) ? record : null);

    public Task<IReadOnlyList<PresenceRecord>> ListOnlineAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        var matches = _online.Values.Where(r => r.EngagementId == engagement).ToArray();
        return Task.FromResult<IReadOnlyList<PresenceRecord>>(matches);
    }
}
