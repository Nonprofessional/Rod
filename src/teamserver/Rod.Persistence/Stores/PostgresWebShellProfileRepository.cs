using Rod.CoreState;
using Microsoft.EntityFrameworkCore;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.WebShells;

namespace Rod.Persistence.Stores;

/// <summary>
/// The durable <see cref="IWebShellProfileRepository"/> pair
/// (architecture.md Sec 5.2's Web-shell class): the connection profiles
/// survive a restart beside the implant rows they hang off. Upsert is the
/// register path's whole write shape; the probe stamps the service drives
/// through NoteProbe persist through the same entity.
/// </summary>
internal sealed class PostgresWebShellProfileRepository : IWebShellProfileRepository
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresWebShellProfileRepository(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task SaveAsync(WebShellProfile profile, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.WebShellProfiles.FindAsync(new object[] { profile.ImplantId }, cancellationToken);
        if (existing is null)
            db.WebShellProfiles.Add(profile);
        else
            db.Entry(existing).CurrentValues.SetValues(profile);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WebShellProfile?> FindAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.WebShellProfiles.FindAsync(new object[] { implant }, cancellationToken);
    }

    public async Task<IReadOnlyList<WebShellProfile>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var listed = await db.WebShellProfiles
            .Where(p => p.EngagementId == engagement)
            .OrderBy(p => p.RegisteredAt)
            .ThenBy(p => p.ImplantId)
            .ToListAsync(cancellationToken);
        return listed;
    }

    public async Task RemoveAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (await db.WebShellProfiles.FindAsync(new object[] { implant }, cancellationToken) is { } found)
        {
            db.WebShellProfiles.Remove(found);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
