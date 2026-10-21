using System.Collections.Concurrent;

namespace Rod.CoreState.Collaboration;

/// <summary>
/// The in-memory <see cref="ICredentialStore"/> twin: rows live as long as
/// the process. Reads go through the same port either way, so the Postgres
/// swap changes durability, never behavior.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<CredentialId, CredentialRecord> _records = new();

    public Task AddAsync(CredentialRecord record, CancellationToken cancellationToken = default)
    {
        _records[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<CredentialRecord?> FindAsync(CredentialId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_records.TryGetValue(id, out var record) ? record : null);

    public Task<IReadOnlyList<CredentialRecord>> ListByEngagementAsync(
        EngagementId engagementId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CredentialRecord>>(
            _records.Values
                .Where(r => r.EngagementId == engagementId)
                .OrderBy(r => r.CapturedAt)
                .ThenBy(r => r.Principal, StringComparer.Ordinal)
                .ToArray());

    public Task<IReadOnlyList<CredentialRecord>> ListBySourceTaskAsync(
        EngagementId engagementId,
        TaskId taskId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CredentialRecord>>(
            _records.Values
                .Where(r => r.EngagementId == engagementId && r.SourceTaskId == taskId)
                .ToArray());
}
