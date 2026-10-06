namespace Rod.CoreState.Automation;

/// <summary>
/// The durable home for engagement-scoped automation rules (architecture.md
/// Sec 10.4). Rules persist with the engagement -- the Postgres-backed
/// adapter swaps in when <c>ConnectionStrings:Postgres</c> is set, and the
/// next-fire stamps ride the row, which is what makes a time trigger survive
/// a teamserver restart. In-memory by default, the same posture as the rest
/// of core state.
/// </summary>
public interface IAutomationRuleStore
{
    /// <summary>Upserts a rule (the id is the identity; a firing re-saves its stamps).</summary>
    Task SaveAsync(AutomationRule rule, CancellationToken cancellationToken = default);

    /// <summary>The rule, or null when unknown.</summary>
    Task<AutomationRule?> FindAsync(AutomationRuleId id, CancellationToken cancellationToken = default);

    /// <summary>The rules belonging to one engagement, oldest first.</summary>
    Task<IReadOnlyList<AutomationRule>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every enabled rule across engagements, oldest first. The engine's tick
    /// scan and subscription reconcile both start here; rules are few and the
    /// scan runs every few seconds, so one process-wide read is the whole cost.
    /// </summary>
    Task<IReadOnlyList<AutomationRule>> ListEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a rule. Returns false when it was not stored.</summary>
    Task<bool> RemoveAsync(AutomationRuleId id, CancellationToken cancellationToken = default);
}
