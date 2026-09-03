using System.Collections.Concurrent;

namespace Rod.Audit;

/// <summary>
/// Default <see cref="IPayloadStore"/> (architecture.md Sec 6): payloads
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

    public Task<IReadOnlyList<PayloadRecord>> ListAsync(Guid engagementId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PayloadRecord>>(
            _payloads.Values
                .Where(p => p.EngagementId == engagementId)
                .OrderByDescending(p => p.BuiltAt)
                .Select(p => p with { Content = Array.Empty<byte>() })
                .ToArray());

    public Task<PayloadRecord?> FindByEnvelopeKeyAsync(Guid envelopeKeyId, CancellationToken cancellationToken = default)
        => Task.FromResult(
            _payloads.Values.FirstOrDefault(p => p.EnvelopeKeyId == envelopeKeyId));

    public Task<bool> RemoveAsync(Guid payloadId, Guid engagementId, CancellationToken cancellationToken = default)
        => Task.FromResult(
            _payloads.TryGetValue(payloadId, out var payload)
            && payload.EngagementId == engagementId
            && _payloads.TryRemove(payloadId, out _));
}
