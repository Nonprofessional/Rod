namespace Rod.CoreState.Redirectors;

/// <summary>
/// The redirector fleet's core-state face (architecture.md Sec 8): the
/// enrollment credentials that pre-provision nodes, the enrolled nodes
/// themselves, and their contact reporting. Deployment-scoped by construction
/// -- no method takes an engagement, because a node is machine capacity and
/// engagement scope arrives per front, at the listener it attaches to. The
/// forwarding rules a node runs are derived from listener fronts, not stored
/// here; this port owns identity and liveness only.
/// </summary>
public interface IRedirectorFleet
{
    /// <summary>
    /// Mints a single-use enrollment credential. The plaintext secret is
    /// returned once; the ledger keeps hashes and stamps only. The
    /// credential's name becomes the enrolled node's name.
    /// </summary>
    /// <param name="name">The fleet-roster name for the node this credential will enroll.</param>
    /// <param name="issuedBy">The minting operator.</param>
    /// <param name="issuedAt">Mint time.</param>
    /// <param name="lifetime">Hard lifetime; null suits staging hardware with a day to enroll.</param>
    Task<RedirectorCredential> MintCredentialAsync(
        string name,
        OperatorId issuedBy,
        DateTimeOffset issuedAt,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default);

    /// <summary>The credential ledger, newest first.</summary>
    Task<IReadOnlyList<RedirectorCredentialState>> ListCredentialsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a credential. A revoked credential that never enrolled can
    /// never enroll; one that already did keeps its node (retire the node for
    /// that). Idempotent.
    /// </summary>
    Task RevokeCredentialAsync(RedirectorCredentialId id, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enrolls a node against a credential: consumes the credential
    /// (single-use) and mints the node's durable secret. Throws
    /// <see cref="RedirectorEnrollException"/> with the honest reason when
    /// the credential is unknown, expired, spent, or revoked.
    /// </summary>
    /// <param name="credentialSecret">The credential plaintext, presented once.</param>
    /// <param name="remoteAddress">The address the enrollment arrived from.</param>
    /// <param name="version">The binary's reported version.</param>
    /// <param name="commit">The binary's reported source commit.</param>
    /// <param name="now">Enrollment time.</param>
    Task<EnrolledRedirector> EnrollAsync(
        string credentialSecret,
        string? remoteAddress,
        string? version,
        string? commit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates a reconnecting node. Throws
    /// <see cref="RedirectorNodeRefusedException"/> when the id is unknown or
    /// the node is retired; a wrong secret reads as unknown (no oracle beyond
    /// "not yours").
    /// </summary>
    Task<RedirectorNode> AuthenticateAsync(
        RedirectorNodeId id,
        string nodeSecret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a contact. A heartbeat, an ack, any message counts; the roster
    /// reads the trail. Unknown ids are ignored -- a touch racing a retire is
    /// not an error worth a round trip.
    /// </summary>
    Task TouchAsync(
        RedirectorNodeId id,
        string? remoteAddress,
        string? version,
        string? commit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>The enrolled fleet, newest first.</summary>
    Task<IReadOnlyList<RedirectorNode>> ListNodesAsync(CancellationToken cancellationToken = default);

    /// <summary>One node, or null when the id is unknown.</summary>
    Task<RedirectorNode?> FindAsync(RedirectorNodeId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires a node. Idempotent. A retired node's reconnect is refused and
    /// its stored rules stop being pushed; retirement is the node-side
    /// revocation (the refusal at the next hello is the enforcement).
    /// </summary>
    Task RetireAsync(RedirectorNodeId id, DateTimeOffset now, CancellationToken cancellationToken = default);
}
