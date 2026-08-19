using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IImplantRepository"/> (ADR 0003). Each call
/// creates a short-lived <see cref="RodPersistenceDbContext"/> from the factory,
/// performs its work, and disposes the context; the context is never held across
/// calls so the singleton adapter stays safe for concurrent use. Implants are
/// keyed by their typed id (mapped to a uuid column); parentage and retirement
/// round-trip through the same row.
/// </summary>
internal sealed class PostgresImplantRepository : IImplantRepository
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresImplantRepository(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task<Implant?> FindAsync(ImplantId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Implants.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Implant>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // Oldest first, matching the in-memory adapter. The engagement column is
        // indexed (ImplantConfiguration), so the scoped read stays cheap.
        return await db.Implants
            .AsNoTracking()
            .Where(i => i.EngagementId == engagementId)
            .OrderBy(i => i.CreatedAt)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Implant>> ListFrontedPivotsAsync(
        ImplantId parent,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // The fronted set (architecture.md Sec 5.2): Pivot-class children of
        // the parent. A fleet's implants table is engagement-sized, so the
        // unindexed parentage scan the fronting claim runs when the parent's
        // own queue is empty stays cheap.
        return await db.Implants
            .AsNoTracking()
            .Where(i => i.ParentImplantId == parent && i.Class == ImplantClass.Pivot)
            .ToArrayAsync(cancellationToken);
    }

    public async Task SaveAsync(Implant implant, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        // Insert-or-replace by id: the in-memory adapter's overwrite behavior.
        // Retirement is persisted by saving the same mutated instance; SetValues
        // mirrors the current scalar state (including RetiredAt) onto the stored
        // row through field access.
        var existing = await db.Implants.FindAsync(new object?[] { implant.Id }, cancellationToken);
        if (existing is null)
            db.Implants.Add(implant);
        else
            db.Entry(existing).CurrentValues.SetValues(implant);

        await db.SaveChangesAsync(cancellationToken);
    }
}
