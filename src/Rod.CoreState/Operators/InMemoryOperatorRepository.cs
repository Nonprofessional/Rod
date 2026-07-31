using System.Collections.Concurrent;

namespace Rod.CoreState.Operators;

/// <summary>
/// In-memory <see cref="IOperatorRepository"/> for the walking skeleton
/// (roadmap M1 -- no Postgres yet). State lives in process and is lost on
/// restart; the port keeps callers agnostic to that.
/// </summary>
public sealed class InMemoryOperatorRepository : IOperatorRepository
{
    private readonly ConcurrentDictionary<OperatorId, Operator> _operators = new();

    public Task<Operator?> FindAsync(OperatorId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_operators.TryGetValue(id, out var op) ? op : null);

    public async Task<Operator> GetOrThrowAsync(OperatorId id, CancellationToken cancellationToken = default)
        => await FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Operator {id} does not exist.");

    public Task SaveAsync(Operator @operator, CancellationToken cancellationToken = default)
    {
        _operators[@operator.Id] = @operator;
        return Task.CompletedTask;
    }
}
