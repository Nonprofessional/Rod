using System.Collections.Concurrent;

namespace Rod.CoreState.Operators;

/// <summary>
/// In-memory <see cref="IOperatorCredentialStore"/> for the walking skeleton and
/// tests. Holds the hash strings in process and is lost on restart; the port
/// keeps callers agnostic to that. See <see cref="IOperatorCredentialStore"/> for
/// the hash-only contract.
/// </summary>
public sealed class InMemoryOperatorCredentialStore : IOperatorCredentialStore
{
    private readonly ConcurrentDictionary<OperatorId, string> _hashes = new();

    public Task<string?> FindHashAsync(OperatorId operatorId, CancellationToken cancellationToken = default)
        => Task.FromResult(_hashes.TryGetValue(operatorId, out var hash) ? hash : null);

    public Task SetHashAsync(OperatorId operatorId, string passwordHash, CancellationToken cancellationToken = default)
    {
        _hashes[operatorId] = passwordHash;
        return Task.CompletedTask;
    }

    public Task RevokeAsync(OperatorId operatorId, CancellationToken cancellationToken = default)
    {
        _hashes.TryRemove(operatorId, out _);
        return Task.CompletedTask;
    }
}
