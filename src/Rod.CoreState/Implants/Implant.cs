using Rod.CoreState.Engagements;

namespace Rod.CoreState.Implants;

/// <summary>
/// An implant -- a short-lived, disposable payload enrolled into exactly one
/// engagement (architecture.md Sec 5). Untrusted by default; carries a unique
/// per-implant key and a kill date. Its identity is bound to its engagement and
/// disposable with it; an implant certificate binds
/// <c>(implant_id, engagement_id)</c> (architecture.md Sec 9).
///
/// Entity shape only at this milestone: <see cref="Key"/> and
/// <see cref="KillDate"/> are recorded here and surfaced on the issued
/// certificate, but their enforcement (refusing to run past the kill date, key
/// rotation) is M4.2.
/// </summary>
public sealed class Implant
{
    public ImplantId Id { get; }
    public EngagementId EngagementId { get; }
    public string Key { get; }
    public DateTimeOffset KillDate { get; }
    public ImplantClass Class { get; }
    public DateTimeOffset CreatedAt { get; }

    private Implant(
        ImplantId id,
        EngagementId engagementId,
        string key,
        DateTimeOffset killDate,
        ImplantClass @class,
        DateTimeOffset createdAt)
    {
        Id = id;
        EngagementId = engagementId;
        Key = key;
        KillDate = killDate;
        Class = @class;
        CreatedAt = createdAt;
    }

    /// <summary>
    /// Factory for a newly enrolled implant. <paramref name="key"/> is the
    /// server-generated per-implant key (base64url); <paramref name="killDate"/>
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
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Implant key is required.", nameof(key));
        if (killDate <= createdAt)
            throw new ArgumentException("Implant kill date must be after creation.", nameof(killDate));

        return new Implant(id, engagementId, key, killDate, @class, createdAt);
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
