namespace Rod.Audit;

/// <summary>
/// Payload store port: the engagement-scoped repository of built implant
/// payloads the operator retrieves after a build (architecture.md Sec 6). The
/// default is an in-memory implementation; the durable file-backed
/// adapter mirrors <see cref="FileArtifactStore"/> under the same
/// <c>Audit:DataDirectory</c> opt-in. Engagement scoping is the caller's
/// discipline: <see cref="FindAsync"/> filters on the engagement id, so
/// cross-engagement access never returns another engagement's payload by
/// construction.
/// </summary>
public interface IPayloadStore
{
    /// <summary>Saves <paramref name="payload"/>; it becomes retrievable by id within its engagement.</summary>
    Task SaveAsync(PayloadRecord payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// A payload by id within <paramref name="engagementId"/>, or null when no
    /// such payload exists in that engagement.
    /// </summary>
    Task<PayloadRecord?> FindAsync(Guid payloadId, Guid engagementId, CancellationToken cancellationToken = default);
}