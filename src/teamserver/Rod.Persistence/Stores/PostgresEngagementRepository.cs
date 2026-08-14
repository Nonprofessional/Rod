using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Engagements;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IEngagementRepository"/> (ADR 0003). Each call
/// creates a short-lived <see cref="RodPersistenceDbContext"/> from the factory,
/// performs its work, and disposes the context. On save the aggregate is
/// inserted if new, or its scalar columns are overwritten when it already exists.
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
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
    }

    public async Task<Engagement> GetOrThrowAsync(EngagementId id, CancellationToken cancellationToken = default)
        => await FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Engagement {id} does not exist.");

    public async Task<IReadOnlyList<Engagement>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // Oldest first, matching the in-memory adapter ().
        return await db.Engagements
            .AsNoTracking()
            .OrderBy(e => e.CreatedAt)
            .ToArrayAsync(cancellationToken);
    }

    public async Task SaveAsync(Engagement engagement, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        // Insert-or-replace by id. For an existing engagement, attach the
        // incoming aggregate as the tracked entity and overwrite its scalar
        // columns; the engagement carries only scalars now.
        var existing = await db.Engagements
            .AsTracking()
            .FirstOrDefaultAsync(e => e.Id == engagement.Id, cancellationToken);

        if (existing is null)
        {
            db.Engagements.Add(engagement);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(engagement);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
