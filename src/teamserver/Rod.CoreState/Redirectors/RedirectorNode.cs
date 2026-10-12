namespace Rod.CoreState.Redirectors;

/// <summary>
/// An enrolled redirector node (architecture.md Sec 8): one machine of the
/// deployment's forwarding capacity. A node is deployment-scoped -- it
/// belongs to no engagement and holds no engagement data; the forwarding
/// rules it runs arrive from the control plane, and engagement scope is
/// granted per front by attaching one to a listener. Presence is the control
/// connection itself; <see cref="LastSeenAt"/> is the reporting trail.
/// </summary>
public sealed record RedirectorNode
{
    /// <summary>Server-assigned node identifier.</summary>
    public required RedirectorNodeId Id { get; init; }

    /// <summary>
    /// The node's name. Inherited from the enrollment credential that minted
    /// it, so the name an operator gives the credential is the name the fleet
    /// roster shows.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The operator who minted the credential the node enrolled with.</summary>
    public required OperatorId EnrolledBy { get; init; }

    public required DateTimeOffset EnrolledAt { get; init; }

    /// <summary>
    /// Stamp of the retirement. A retired node is refused at reconnect and
    /// its live control connection is closed; retirement is idempotent.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; init; }

    /// <summary>The last contact the control plane heard from the node.</summary>
    public DateTimeOffset LastSeenAt { get; init; }

    /// <summary>The remote address the last contact arrived from.</summary>
    public string? LastAddress { get; init; }

    /// <summary>The version the node reported at its last contact.</summary>
    public string? ReportedVersion { get; init; }

    /// <summary>
    /// The source commit the node reported at its last contact -- the same
    /// build stamp the binary prints, so fleet provenance is read off the
    /// node itself, not off deployment notes.
    /// </summary>
    public string? ReportedCommit { get; init; }

    /// <summary>
    /// SHA-256 digest of the node secret, hash-only like every stored
    /// credential in the platform. The plaintext exists once, in the
    /// enrollment response the node persists beside its binary.
    /// </summary>
    public required string SecretDigest { get; init; }
}

/// <summary>
/// The mint result for an enrollment credential: the plaintext
/// <see cref="Secret"/> is handed back exactly once, at mint time, and the
/// ledger keeps only hashes and stamps afterwards.
/// </summary>
public sealed record RedirectorCredential
{
    public required RedirectorCredentialId Id { get; init; }

    /// <summary>
    /// The plaintext secret, base64url-encoded. Present only on the mint
    /// result; never persisted in the clear. The name the operator gives the
    /// credential becomes the enrolled node's name.
    /// </summary>
    public required string Secret { get; init; }

    public required string Name { get; init; }

    public required OperatorId IssuedBy { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>Hard expiry; the credential cannot enroll after this instant.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// The credential's inspectable state: the stamps and the enrollment that
/// spent it. No secret rides here.
/// </summary>
public sealed record RedirectorCredentialState
{
    public required RedirectorCredentialId Id { get; init; }

    public required string Name { get; init; }

    public required OperatorId IssuedBy { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The node this credential enrolled, once enrollment happened. An
    /// enrollment credential is single-use by design ("enroll once against
    /// pre-provisioned credential", architecture.md Sec 8): after this is
    /// set, further presentations read as spent.
    /// </summary>
    public RedirectorNodeId? EnrolledNodeId { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }
}

/// <summary>
/// The result of a successful <see cref="IRedirectorFleet.EnrollAsync"/>: the
/// node the credential minted, plus the node secret whose plaintext exists
/// only here. The node persists the pair and reconnects with it; the server
/// keeps the digest alone.
/// </summary>
public sealed record EnrolledRedirector
{
    public required RedirectorNode Node { get; init; }

    /// <summary>The node secret, base64url-encoded, shown exactly once.</summary>
    public required string NodeSecret { get; init; }
}
