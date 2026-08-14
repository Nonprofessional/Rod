using Microsoft.EntityFrameworkCore;
using Rod.Audit;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IArtifactStore"/> (ADR 0003, Phase
/// 4). Each artifact's bytes are stored as a <c>bytea</c> column on
/// <c>artifacts</c> alongside its metadata, so evidence linked to a task outlives
/// a teamserver restart. Behind the same port the in-memory and file adapters
/// serve; no lock is needed -- saving is a single row write with no cross-field
/// atomicity to protect, the same shape as the other read-mostly adapters.
/// </summary>
internal sealed class PostgresArtifactStore : IArtifactStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresArtifactStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task SaveAsync(Artifact artifact, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        // Insert-or-replace by id. The in-memory adapter's overwrite behavior;
        // artifacts are write-once in practice but the contract allows re-saving.
        var existing = await db.Artifacts.FindAsync(new object?[] { artifact.ArtifactId }, cancellationToken);
        if (existing is null)
            db.Artifacts.Add(artifact);
        else
            db.Entry(existing).CurrentValues.SetValues(artifact);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Artifact?> FindAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // The full record including the bytea content -- evidence reads back whole.
        return await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ArtifactId == artifactId, cancellationToken);
    }

    public async Task<IReadOnlyList<Artifact>> ForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Artifacts
            .AsNoTracking()
            .Where(a => a.TaskId == taskId)
            .OrderBy(a => a.StoredAt)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Artifact>> ListAsync(Guid engagementId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Artifacts
            .AsNoTracking()
            .Where(a => a.EngagementId == engagementId)
            .OrderBy(a => a.StoredAt)
            .ToArrayAsync(cancellationToken);
    }
}
