using Rod.CoreState.Engagements;

namespace Rod.CoreState.Implants;

/// <summary>
/// An implant -- a short-lived, disposable payload enrolled into exactly one
/// engagement (architecture.md Sec 5). Untrusted by default; carries a kill
/// date and no key material -- its cryptographic identity is the keypair the
/// implant generated itself, bound to its engagement by the CA-signed leaf at
/// enroll (architecture.md Sec 9), and the entity is disposable with it.
///
/// The kill date is enforced on both sides of the wire. Retirement marks an
/// implant taken out of operation: a retired implant is refused at handshake and
/// untaskable, and its active session is closed when it is retired.
///
/// Parentage: a capable implant can deploy another class on the same host
/// via a deployment verb, and the child enrols into the same engagement and
/// records its parent (architecture.md Sec 5.2). A top-level implant (one
/// enrolled from a stager token) has a null <see cref="ParentImplantId"/>; a
/// child carries its parent's id. The engagement binding of parent and child is
/// enforced by the enrollment use case, not the entity, so this type stays free
/// of the implant registry.
/// </summary>
public sealed class Implant
{
    public ImplantId Id { get; }
    public EngagementId EngagementId { get; }
    public DateTimeOffset KillDate { get; }
    public ImplantClass Class { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? RetiredAt { get; private set; }

    /// <summary>
    /// The operator who deployed this implant -- the one who minted the stager
    /// token a top-level implant redeemed, or the parent's deployer for a child.
    /// Enrollment is implant-initiated, so later implant-initiated events (a
    /// session opening, a task completing) attribute themselves through this
    /// field rather than through a request body (architecture.md Sec 11). The
    /// default <see cref="OperatorId"/> means "unattributed" -- the production
    /// enrollment path always sets it; tests that do not care about attribution
    /// may omit it.
    /// </summary>
    public OperatorId DeployedBy { get; }

    /// <summary>
    /// The implant this one was derived from, or null for a top-level implant
    /// enrolled from a stager token (architecture.md Sec 5.2). A child enrols
    /// into the same engagement as its parent; that binding is checked by the
    /// enrollment use case, which is the only caller of <see cref="EnrollChild"/>.
    /// </summary>
    public ImplantId? ParentImplantId { get; }

    /// <summary>True once the implant has been taken out of operation.</summary>
    public bool IsRetired => RetiredAt is not null;

    private Implant(
        ImplantId id,
        EngagementId engagementId,
        DateTimeOffset killDate,
        ImplantClass @class,
        DateTimeOffset createdAt,
        OperatorId deployedBy,
        ImplantId? parentImplantId)
    {
        Id = id;
        EngagementId = engagementId;
        KillDate = killDate;
        Class = @class;
        CreatedAt = createdAt;
        DeployedBy = deployedBy;
        ParentImplantId = parentImplantId;
    }

    /// <summary>
    /// Factory for a newly enrolled top-level implant (architecture.md Sec 5):
    /// one enrolled from a stager token, with no parent. The implant carries no
    /// key material -- its cryptographic identity is the keypair it generated
    /// itself, bound to the engagement by the CA-signed leaf at enroll
    /// (architecture.md Sec 9), so there is nothing here to store or leak.
    /// <paramref name="killDate"/> is the hard self-termination timestamp.
    /// <paramref name="deployedBy"/> is the operator who authorized the deployment
    /// (the token issuer); it defaults to unattributed so tests that do not care
    /// about attribution stay unchanged, while the production enrollment path
    /// always sets it.
    /// </summary>
    public static Implant Enroll(
        ImplantId id,
        EngagementId engagementId,
        DateTimeOffset killDate,
        ImplantClass @class,
        DateTimeOffset createdAt,
        OperatorId deployedBy = default)
        => EnrollChild(id, engagementId, killDate, @class, createdAt, deployedBy, parentImplantId: null);

    /// <summary>
    /// Factory for a child implant derived from <paramref name="parentImplantId"/>
    /// (architecture.md Sec 5.2). The child enrols into the parent's
    /// engagement and records its parent; the same kill-date shape as a
    /// top-level implant applies. A null <paramref name="parentImplantId"/> yields
    /// a top-level implant, so <see cref="Enroll"/> delegates here. The caller (the
    /// enrollment use case) is responsible for resolving and scope-checking the
    /// parent; this factory only records the linkage. <paramref name="deployedBy"/>
    /// is the operator who authorized the deployment; it defaults to unattributed
    /// so tests that do not care about attribution stay unchanged.
    /// </summary>
    public static Implant EnrollChild(
        ImplantId id,
        EngagementId engagementId,
        DateTimeOffset killDate,
        ImplantClass @class,
        DateTimeOffset createdAt,
        OperatorId deployedBy = default,
        ImplantId? parentImplantId = null)
    {
        if (killDate <= createdAt)
            throw new ArgumentException("Implant kill date must be after creation.", nameof(killDate));
        // A default ImplantId (Guid.Empty) is never a real parent; only a non-default
        // id or null is valid, so a caller cannot accidentally record an empty linkage.
        if (parentImplantId is { } parent && parent == default)
            throw new ArgumentException("Parent implant id must be a non-default identifier.", nameof(parentImplantId));

        return new Implant(id, engagementId, killDate, @class, createdAt, deployedBy, parentImplantId);
    }

    /// <summary>
    /// Takes the implant out of operation (architecture.md Sec 7). Sets
    /// <see cref="RetiredAt"/>; a retired implant is refused at handshake and
    /// untaskable thereafter. Idempotent: a second call on an already-retired
    /// implant returns false and changes nothing, so the retire use case can
    /// distinguish "just retired" from "was already retired". The session is
    /// closed by the use case, not the entity, so this type stays free of the
    /// session registry.
    /// </summary>
    public bool Retire(DateTimeOffset at)
    {
        if (RetiredAt is not null)
            return false;

        RetiredAt = at;
        return true;
    }
}

/// <summary>
/// Implant classes, by operational purpose rather than "device flavor"
/// (architecture.md Sec 5.2).
/// </summary>
public enum ImplantClass
{
    /// <summary>The primary long-haul implant; full capability set.</summary>
    Stage2,

    /// <summary>A tiny stage-1 loader that fetches a stage-2 implant.</summary>
    Stager,

    /// <summary>A script in a web root, bound to the web transport.</summary>
    WebShell,

    /// <summary>A short-lived, TTL'd implant from a one-liner bootstrap.</summary>
    Ephemeral,

    /// <summary>Represents a host that cannot run its own implant; forwards tasking.</summary>
    Pivot,
}
