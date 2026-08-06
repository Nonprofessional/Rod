using Rod.CoreState.Engagements;

namespace Rod.CoreState.Implants;

/// <summary>
/// An implant -- a short-lived, disposable payload enrolled into exactly one
/// engagement (architecture.md Sec 5). Untrusted by default; carries a unique
/// per-implant key and a kill date. Its identity is bound to its engagement and
/// disposable with it; an implant certificate binds
/// <c>(implant_id, engagement_id)</c> (architecture.md Sec 9).
///
/// The kill date is enforced on both sides of the wire (M4.2); per-implant keys
/// are server-generated at enrollment and build time. Retirement (M4.4) marks an
/// implant taken out of operation: a retired implant is refused at handshake and
/// untaskable, and its active session is closed when it is retired.
///
/// Parentage (M5.2): a capable implant can deploy another class on the same host
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
    public string Key { get; }
    public DateTimeOffset KillDate { get; }
    public ImplantClass Class { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? RetiredAt { get; private set; }

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
        string key,
        DateTimeOffset killDate,
        ImplantClass @class,
        DateTimeOffset createdAt,
        ImplantId? parentImplantId)
    {
        Id = id;
        EngagementId = engagementId;
        Key = key;
        KillDate = killDate;
        Class = @class;
        CreatedAt = createdAt;
        ParentImplantId = parentImplantId;
    }

    /// <summary>
    /// Factory for a newly enrolled top-level implant (architecture.md Sec 5):
    /// one enrolled from a stager token, with no parent. <paramref name="key"/> is
    /// the server-generated per-implant key (base64url); <paramref name="killDate"/>
    /// is the hard self-termination timestamp. Both are produced by the enrollment
    /// service, not the entity, so this type stays free of crypto.
    /// </summary>
    public static Implant Enroll(
        ImplantId id,
        EngagementId engagementId,
        string key,
        DateTimeOffset killDate,
        ImplantClass @class,
        DateTimeOffset createdAt)
        => EnrollChild(id, engagementId, key, killDate, @class, createdAt, parentImplantId: null);

    /// <summary>
    /// Factory for a child implant derived from <paramref name="parentImplantId"/>
    /// (architecture.md Sec 5.2, roadmap M5.2). The child enrols into the parent's
    /// engagement and records its parent; the same key/kill-date shape as a
    /// top-level implant applies. A null <paramref name="parentImplantId"/> yields
    /// a top-level implant, so <see cref="Enroll"/> delegates here. The caller (the
    /// enrollment use case) is responsible for resolving and scope-checking the
    /// parent; this factory only records the linkage.
    /// </summary>
    public static Implant EnrollChild(
        ImplantId id,
        EngagementId engagementId,
        string key,
        DateTimeOffset killDate,
        ImplantClass @class,
        DateTimeOffset createdAt,
        ImplantId? parentImplantId)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Implant key is required.", nameof(key));
        if (killDate <= createdAt)
            throw new ArgumentException("Implant kill date must be after creation.", nameof(killDate));
        // A default ImplantId (Guid.Empty) is never a real parent; only a non-default
        // id or null is valid, so a caller cannot accidentally record an empty linkage.
        if (parentImplantId is { } parent && parent == default)
            throw new ArgumentException("Parent implant id must be a non-default identifier.", nameof(parentImplantId));

        return new Implant(id, engagementId, key, killDate, @class, createdAt, parentImplantId);
    }

    /// <summary>
    /// Takes the implant out of operation (architecture.md Sec 7, M4.4). Sets
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
