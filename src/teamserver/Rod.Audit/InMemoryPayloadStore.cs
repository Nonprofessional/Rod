using System.Collections.Concurrent;

namespace Rod.Audit;

/// <summary>
/// Walking-skeleton <see cref="IPayloadStore"/> (architecture.md Sec 6): payloads
/// live for the process lifetime, keyed by id. The durable file-backed adapter
/// replaces it when <c>Audit:DataDirectory</c> is configured.
/// </summary>
public sealed class InMemoryPayloadStore : IPayloadStore
{
    private readonly ConcurrentDictionary<Guid, PayloadRecord> _payloads = new();

    public Task SaveAsync(PayloadRecord payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _payloads[payload.PayloadId] = payload;
        return Task.CompletedTask;
    }

    public Task<PayloadRecord?> FindAsync(Guid payloadId, Guid engagementId, CancellationToken cancellationToken = default)
        => Task.FromResult(
            _payloads.TryGetValue(payloadId, out var payload) && payload.EngagementId == engagementId
                ? payload
                : null);
}