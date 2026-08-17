namespace Rod.CoreState.Operators;

/// <summary>
/// Persistence port for an operator's password verifier. Stores only the opaque
/// hash produced by the auth layer's password hasher -- never a plaintext
/// password -- keyed by operator id. The domain neither produces nor interprets
/// the value: provisioning hashes a chosen password in the auth layer and calls
/// <see cref="SetHashAsync"/>; login reads the stored hash back and the auth
/// layer verifies it. This is the operator-facing twin of the stager-token
/// hash-only rule (the stager service keeps a digest, never the secret), and
/// like that port the in-memory implementation lives in core state while the
/// durable PostgreSQL adapter lives in Rod.Persistence (ADR 0003).
/// </summary>
/// <remarks>
/// The <see cref="Operator"/> entity stays free of any stored-secret shape: it
/// carries identity only. A password is an auth concern, so its verifier lives
/// behind this port rather than as a field on the aggregate.
/// </remarks>
public interface IOperatorCredentialStore
{
    /// <summary>
    /// Returns the stored password hash for an operator, or null when no
    /// verifier is set (the operator exists but has not been provisioned with a
    /// password).
    /// </summary>
    Task<string?> FindHashAsync(OperatorId operatorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or replaces the password hash for an operator. Called at operator
    /// provisioning -- the bootstrap seed or a future management path -- and
    /// idempotent on repeat calls.
    /// </summary>
    Task SetHashAsync(OperatorId operatorId, string passwordHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes an operator's credential (architecture.md Sec 9 -- certificate
    /// revocation, operator half): deletes the stored verifier, so the next
    /// login attempt fails -- it reads the hash fresh on every attempt, so the
    /// revocation is effective immediately, without a restart. Idempotent;
    /// revoking an operator with no stored verifier succeeds. Re-provisioning
    /// the operator with a new password restores login (<see cref="SetHashAsync"/>).
    /// </summary>
    Task RevokeAsync(OperatorId operatorId, CancellationToken cancellationToken = default);
}
