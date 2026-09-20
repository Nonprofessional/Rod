using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Launchers;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="ILauncherStore"/> (ADR 0003): the rendered
/// launcher rows survive a teamserver restart, so a credential with a
/// twelve-hour window stays re-copyable and revocable across one. Plain
/// rows, no concurrency hazard -- renders are operator-paced.
/// </summary>
internal sealed class PostgresLauncherStore : ILauncherStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresLauncherStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task SaveAsync(Launcher launcher, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Launchers.FindAsync(new object[] { launcher.Id }, cancellationToken);
        if (stored is null)
        {
            db.Launchers.Add(launcher);
        }
        else
        {
            // The only mutation a saved row ever takes: a row re-saved with a
            // revocation stamp carries that stamp to the stored twin.
            if (launcher.RevokedAt is { } at)
                stored.Revoke(at);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Launcher?> FindAsync(LauncherId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Launchers.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Launcher>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Launchers.AsNoTracking()
            .Where(l => l.EngagementId == engagement)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(cancellationToken);
        return rows;
    }

    public async Task<bool> RemoveAsync(LauncherId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Launchers.FindAsync(new object[] { id }, cancellationToken);
        if (stored is null)
            return false;
        db.Launchers.Remove(stored);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
