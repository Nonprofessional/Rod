using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.Persistence.Configurations;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IEngagementMembershipStore"/> (ADR 0003):
/// granted memberships survive a teamserver restart. Plain rows, no
/// concurrency hazard -- the owner-only member routes serialize mutations
/// per process, and a second teamserver against one database is not a
/// supported shape (the posture the listener store keeps).
/// </summary>
internal sealed class PostgresEngagementMembershipStore : IEngagementMembershipStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresEngagementMembershipStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task<EngagementMembership?> FindAsync(
        EngagementId engagementId,
        OperatorId operatorId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.EngagementMembers.AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.EngagementId == engagementId && m.OperatorId == operatorId,
                cancellationToken);
        return stored is null ? null : ToMembership(stored);
    }

    public async Task<IReadOnlyList<EngagementMembership>> ListAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.EngagementMembers.AsNoTracking()
            .Where(m => m.EngagementId == engagementId)
            .OrderBy(m => m.OperatorId.Value)
            .ToListAsync(cancellationToken);
        return rows.Select(ToMembership).ToArray();
    }

    public async Task<IReadOnlyList<EngagementMembership>> ListForOperatorAsync(
        OperatorId operatorId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.EngagementMembers.AsNoTracking()
            .Where(m => m.OperatorId == operatorId)
            .ToListAsync(cancellationToken);
        return rows.Select(ToMembership).ToArray();
    }

    public async Task SaveAsync(EngagementMembership membership, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.EngagementMembers.FindAsync(
            new object[] { membership.EngagementId, membership.OperatorId }, cancellationToken);
        if (stored is null)
        {
            db.EngagementMembers.Add(new StoredEngagementMembership
            {
                EngagementId = membership.EngagementId,
                OperatorId = membership.OperatorId,
                Role = membership.Role,
                AddedAt = membership.AddedAt,
            });
        }
        else
        {
            stored.Role = membership.Role;
            stored.AddedAt = membership.AddedAt;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RemoveAsync(
        EngagementId engagementId,
        OperatorId operatorId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.EngagementMembers.FindAsync(
            new object[] { engagementId, operatorId }, cancellationToken);
        if (stored is null)
            return false;

        db.EngagementMembers.Remove(stored);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static EngagementMembership ToMembership(StoredEngagementMembership stored)
        => new(stored.EngagementId, stored.OperatorId, stored.Role, stored.AddedAt);
}
