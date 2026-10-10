using System.Collections.Concurrent;

namespace Rod.CoreState.Engagements;

/// <summary>
/// In-memory <see cref="IEngagementMembershipStore"/>: the default adapter.
/// State lives in process and is lost on restart (the durable Postgres twin
/// replaces it through the composition root); the port keeps callers agnostic
/// to that, the same posture every other core-state store keeps.
/// </summary>
public sealed class InMemoryEngagementMembershipStore : IEngagementMembershipStore
{
    private readonly ConcurrentDictionary<(EngagementId Engagement, OperatorId Operator), EngagementMembership> _members = new();

    public Task<EngagementMembership?> FindAsync(
        EngagementId engagementId,
        OperatorId operatorId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_members.TryGetValue((engagementId, operatorId), out var membership)
            ? membership
            : null);

    public Task<IReadOnlyList<EngagementMembership>> ListAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EngagementMembership>>(
            _members.Values
                .Where(m => m.EngagementId == engagementId)
                .OrderBy(m => m.OperatorId.Value)
                .ToArray());

    public Task<IReadOnlyList<EngagementMembership>> ListForOperatorAsync(
        OperatorId operatorId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EngagementMembership>>(
            _members.Values
                .Where(m => m.OperatorId == operatorId)
                .ToArray());

    public Task SaveAsync(EngagementMembership membership, CancellationToken cancellationToken = default)
    {
        _members[(membership.EngagementId, membership.OperatorId)] = membership;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(
        EngagementId engagementId,
        OperatorId operatorId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_members.TryRemove((engagementId, operatorId), out _));
}
