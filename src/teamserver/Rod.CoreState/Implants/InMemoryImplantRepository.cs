using System.Collections.Concurrent;
using Rod.CoreState.Engagements;

namespace Rod.CoreState.Implants;

/// <summary>
/// In-memory <see cref="IImplantRepository"/> by default.
/// ( -- no Postgres yet). State lives in process and is lost on
/// restart; the port keeps callers agnostic to that.
/// </summary>
public sealed class InMemoryImplantRepository : IImplantRepository
{
    private readonly ConcurrentDictionary<ImplantId, Implant> _implants = new();

    public Task<Implant?> FindAsync(ImplantId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_implants.TryGetValue(id, out var implant) ? implant : null);

    public Task<IReadOnlyList<Implant>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
    {
        var matches = _implants.Values
            .Where(i => i.EngagementId == engagementId)
            .OrderBy(i => i.CreatedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<Implant>>(matches);
    }

    public Task<IReadOnlyList<Implant>> ListFrontedPivotsAsync(
        ImplantId parent,
        CancellationToken cancellationToken = default)
    {
        // The fronted set (architecture.md Sec 5.2): Pivot-class children of
        // the parent, whatever their enrollment order -- the fronting claim
        // merges across targets by task-queue order, not by this listing.
        var fronted = _implants.Values
            .Where(i => i.ParentImplantId == parent && i.Class == ImplantClass.Pivot)
            .ToArray();
        return Task.FromResult<IReadOnlyList<Implant>>(fronted);
    }

    public Task SaveAsync(Implant implant, CancellationToken cancellationToken = default)
    {
        _implants[implant.Id] = implant;
        return Task.CompletedTask;
    }
}
