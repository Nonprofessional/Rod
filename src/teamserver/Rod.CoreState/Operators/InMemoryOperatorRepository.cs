using System.Collections.Concurrent;

namespace Rod.CoreState.Operators;

/// <summary>
/// In-memory <see cref="IOperatorRepository"/> for the walking skeleton
/// ( -- no Postgres yet). State lives in process and is lost on
/// restart; the port keeps callers agnostic to that.
/// </summary>
public sealed class InMemoryOperatorRepository : IOperatorRepository
{
    private readonly ConcurrentDictionary<OperatorId, Operator> _operators = new();

    public Task<Operator?> FindAsync(OperatorId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_operators.TryGetValue(id, out var op) ? op : null);

    public Task<Operator?> FindByHandleAsync(string handle, CancellationToken cancellationToken = default)
    {
        // The dictionary is keyed by id, so a by-handle lookup is a scan. Handles
        // are unique by provisioning, so the first match is the match; the store
        // is small and login is low-frequency, so the scan is not a hot path.
        if (string.IsNullOrWhiteSpace(handle))
            return Task.FromResult<Operator?>(null);

        var match = _operators.Values.FirstOrDefault(o => o.Handle == handle.Trim());
        return Task.FromResult(match);
    }

    public async Task<Operator> GetOrThrowAsync(OperatorId id, CancellationToken cancellationToken = default)
        => await FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Operator {id} does not exist.");

    public Task SaveAsync(Operator @operator, CancellationToken cancellationToken = default)
    {
        _operators[@operator.Id] = @operator;
        return Task.CompletedTask;
    }
}
