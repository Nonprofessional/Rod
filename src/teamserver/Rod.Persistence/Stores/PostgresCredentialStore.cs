using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.Persistence.Configurations;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="ICredentialStore"/>: harvested rows
/// survive a teamserver restart. Rows are append-only, landed once by the
/// harvest path, so there is no update shape to hold.
/// </summary>
internal sealed class PostgresCredentialStore : ICredentialStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresCredentialStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task AddAsync(CredentialRecord record, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        db.Credentials.Add(StoredCredential.From(record));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CredentialRecord?> FindAsync(CredentialId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Credentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id.Value, cancellationToken);
        return stored?.ToDomain();
    }

    public async Task<IReadOnlyList<CredentialRecord>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Credentials.AsNoTracking()
            .Where(c => c.EngagementId == engagementId)
            .OrderBy(c => c.CapturedAt)
            .ThenBy(c => c.Principal)
            .ToListAsync(cancellationToken);
        return rows.Select(c => c.ToDomain()).ToArray();
    }

    public async Task<IReadOnlyList<CredentialRecord>> ListBySourceTaskAsync(
        EngagementId engagementId,
        TaskId taskId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Credentials.AsNoTracking()
            .Where(c => c.EngagementId == engagementId && c.SourceTaskId == taskId)
            .ToListAsync(cancellationToken);
        return rows.Select(c => c.ToDomain()).ToArray();
    }
}
