namespace Rod.CoreState.Staging;

/// <summary>
/// Mints and (later, M1.2) redeems stager tokens. The mint result carries the
/// plaintext secret exactly once; only a salted hash is retained server-side so
/// a stolen store cannot replay tokens.
/// </summary>
public interface IStagerTokenService
{
    /// <summary>
    /// Mints a fresh stager token for <paramref name="engagementId"/>, issued by
    /// <paramref name="issuedBy"/>. The returned secret is shown once.
    /// </summary>
    Task<StagerToken> MintAsync(
        EngagementId engagementId,
        OperatorId issuedBy,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default);
}
