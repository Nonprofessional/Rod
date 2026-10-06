using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Automation;
using Rod.Persistence.Configurations;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IAutomationRuleStore"/> (architecture.md
/// Sec 10.4): engagement-scoped automation rules survive a teamserver
/// restart, next-fire stamps riding the row -- the durability time triggers
/// are designed around. Plain rows, no concurrency hazard: firings serialize
/// in the engine and mutations in the rule service, and a second teamserver
/// against the same database is not a supported shape.
/// </summary>
internal sealed class PostgresAutomationRuleStore : IAutomationRuleStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresAutomationRuleStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task SaveAsync(AutomationRule rule, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.AutomationRules.FindAsync(new object[] { rule.Id.Value }, cancellationToken);
        if (stored is null)
        {
            db.AutomationRules.Add(StoredAutomationRule.From(rule));
        }
        else
        {
            // The row mirrors the aggregate whole; the id and the engagement
            // binding never change, everything else may have.
            db.Entry(stored).CurrentValues.SetValues(StoredAutomationRule.From(rule));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AutomationRule?> FindAsync(AutomationRuleId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.AutomationRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id.Value, cancellationToken);
        return stored?.ToDomain();
    }

    public async Task<IReadOnlyList<AutomationRule>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var mine = await db.AutomationRules.AsNoTracking()
            .Where(r => r.EngagementId == engagementId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        return mine.Select(r => r.ToDomain()).ToArray();
    }

    public async Task<IReadOnlyList<AutomationRule>> ListEnabledAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var enabled = await db.AutomationRules.AsNoTracking()
            .Where(r => r.Enabled)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        return enabled.Select(r => r.ToDomain()).ToArray();
    }

    public async Task<bool> RemoveAsync(AutomationRuleId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.AutomationRules.FindAsync(new object[] { id.Value }, cancellationToken);
        if (stored is null)
            return false;
        db.AutomationRules.Remove(stored);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
