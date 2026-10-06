using System.Collections.Concurrent;

namespace Rod.CoreState.Automation;

/// <summary>
/// The in-memory <see cref="IAutomationRuleStore"/> twin: rules live as long
/// as the process. The engine reads through the same port either way, so the
/// Postgres swap changes durability, never behavior.
/// </summary>
public sealed class InMemoryAutomationRuleStore : IAutomationRuleStore
{
    private readonly ConcurrentDictionary<AutomationRuleId, AutomationRule> _rules = new();

    public Task SaveAsync(AutomationRule rule, CancellationToken cancellationToken = default)
    {
        _rules[rule.Id] = rule;
        return Task.CompletedTask;
    }

    public Task<AutomationRule?> FindAsync(AutomationRuleId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_rules.TryGetValue(id, out var rule) ? rule : null);

    public Task<IReadOnlyList<AutomationRule>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AutomationRule>>(
            _rules.Values
                .Where(r => r.EngagementId == engagementId)
                .OrderBy(r => r.CreatedAt)
                .ThenBy(r => r.Id.Value)
                .ToArray());

    public Task<IReadOnlyList<AutomationRule>> ListEnabledAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AutomationRule>>(
            _rules.Values
                .Where(r => r.Enabled)
                .OrderBy(r => r.CreatedAt)
                .ThenBy(r => r.Id.Value)
                .ToArray());

    public Task<bool> RemoveAsync(AutomationRuleId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_rules.TryRemove(id, out _));
}
