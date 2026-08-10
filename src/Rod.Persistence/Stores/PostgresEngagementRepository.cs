using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Engagements;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IEngagementRepository"/> (ADR 0003). Each call
/// creates a short-lived <see cref="RodPersistenceDbContext"/> from the factory,
/// performs its work, and disposes the context. Membership is an owned collection
/// included on every read so the aggregate reloads whole. On save the aggregate
/// is inserted if new, or the stored row is replaced (scalars overwritten, the
/// owned membership rewritten) when it already exists -- the
/// <c>(engagement, operator)</c> composite key is the natural per-row identity.
/// </summary>
internal sealed class PostgresEngagementRepository : IEngagementRepository
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresEngagementRepository(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task<Engagement?> FindAsync(EngagementId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Engagements
            .AsNoTracking()
            .Include(e => e.Members)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
    }

    public async Task<Engagement> GetOrThrowAsync(EngagementId id, CancellationToken cancellationToken = default)
        => await FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Engagement {id} does not exist.");

    public async Task<IReadOnlyList<Engagement>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // Oldest first, matching the in-memory adapter (roadmap M1.5).
        return await db.Engagements
            .AsNoTracking()
            .Include(e => e.Members)
            .OrderBy(e => e.CreatedAt)
            .ToArrayAsync(cancellationToken);
    }

    public async Task SaveAsync(Engagement engagement, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        // Insert-or-replace by id. For an existing engagement, attach the incoming
        // aggregate as the tracked entity and mark it (and its owned membership)
        // modified: EF rewrites the scalar columns and re-syncs the owned rows.
        // The aggregate's members have no identity outside their (engagement,
        // operator) key, so a replace is the simplest correct mirror of the
        // aggregate's current membership -- the aggregate owns the invariants
        // (one owner, no duplicates); the store only persists what it is handed.
        var existing = await db.Engagements
            .Include(e => e.Members)
            .AsTracking()
            .FirstOrDefaultAsync(e => e.Id == engagement.Id, cancellationToken);

        if (existing is null)
        {
            db.Engagements.Add(engagement);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(engagement);
            ReconcileMembers(db, existing, engagement);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // Replays the aggregate's membership onto the tracked entity through EF's
    // change tracker, so adds/updates/removes translate to row inserts/updates/
    // deletes keyed by (engagement, operator). The Members property is read-only,
    // so the collection value is read off the tracked entity's navigation (which
    // the field-access mode keeps as the private _members List) and mutated in
    // place; EF records the deltas on SaveChanges.
    private static void ReconcileMembers(
        RodPersistenceDbContext db,
        Engagement stored,
        Engagement source)
    {
        var currentMembers = (ICollection<EngagementMembership>)db.Entry(stored)
            .Collection(e => e.Members).CurrentValue!;
        var byOperator = currentMembers.ToDictionary(m => m.OperatorId);

        // Drop members the aggregate no longer carries.
        var dropped = currentMembers
            .Where(m => !source.Members.Any(s => s.OperatorId == m.OperatorId))
            .ToArray();
        foreach (var row in dropped)
            currentMembers.Remove(row);

        // Add new members; update roles on existing ones (ChangeMemberRole).
        foreach (var member in source.Members)
        {
            if (byOperator.TryGetValue(member.OperatorId, out var row))
            {
                if (row.Role != member.Role)
                    row.Role = member.Role;
            }
            else
            {
                currentMembers.Add(member);
            }
        }
    }
}
