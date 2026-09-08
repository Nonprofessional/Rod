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

    /// <summary>
    /// The engagement's payloads, newest first, as metadata only -- the bytes
    /// stay wherever the adapter keeps them and are loaded per
    /// <see cref="FindAsync"/>. This is the operator's library view: the
    /// durable answer to the bounded, process-local build-job list.
    /// </summary>
    Task<IReadOnlyList<PayloadRecord>> ListAsync(Guid engagementId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The payload carrying an AES-GCM envelope key under this id, or null
    /// when none does. The enroll decode resolves the key by the id the
    /// encrypted body prefixes; engagement scoping happens inside the body
    /// (the enroll token), so the lookup itself is by key id only.
    /// </summary>
    Task<PayloadRecord?> FindByEnvelopeKeyAsync(Guid envelopeKeyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The payload a build minted <paramref name="tokenId"/> into (its baked
    /// enrollment credential), or null when no stored payload carries it --
    /// a manually minted token names no payload. The enroll path resolves the
    /// build's check-in key through this: a token minted with a payload binds
    /// the enrollment to that artifact's key posture.
    /// </summary>
    Task<PayloadRecord?> FindByTokenAsync(Guid tokenId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a payload from the engagement: the stored bytes and the library
    /// listing are gone, a stager fetching this payload 404s from now on, and
    /// the deletion is the caller's to audit. Returns false when no such
    /// payload exists in that engagement.
    /// </summary>
    Task<bool> RemoveAsync(Guid payloadId, Guid engagementId, CancellationToken cancellationToken = default);
}