namespace Rod.CoreState.Engagements;

/// <summary>
/// An engagement -- the unit of isolation and evidence for one authorized
/// operation (architecture.md Sec 3). Created by one operator (the owner, held
/// in <see cref="OwnerId"/> for accountability); any authenticated operator can
/// operate on it.
/// </summary>
public sealed class Engagement
{
    public EngagementId Id { get; }
    public string Name { get; }
    public OperatorId OwnerId { get; }
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// The engagement's rules-of-engagement scope (architecture.md Sec 9).
    /// Unrestricted until an operator applies a profile; see
    /// <see cref="ApplyRoe"/>.
    /// </summary>
    public RoeProfile Roe { get; private set; } = RoeProfile.Unrestricted;

    private Engagement(
        EngagementId id,
        string name,
        OperatorId ownerId,
        DateTimeOffset createdAt,
        RoeProfile? roe = null)
    {
        Id = id;
        Name = name;
        OwnerId = ownerId;
        CreatedAt = createdAt;
        Roe = roe ?? RoeProfile.Unrestricted;
    }

    /// <summary>
    /// Creates a new engagement owned by <paramref name="ownerId"/>. The owner is
    /// recorded for accountability; access is not role-gated today (any
    /// authenticated operator can operate on any engagement).
    /// </summary>
    public static Engagement Create(
        EngagementId id,
        string name,
        OperatorId ownerId,
        DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Engagement name is required.", nameof(name));

        return new Engagement(id, name.Trim(), ownerId, createdAt);
    }

    /// <summary>
    /// Applies the engagement's rules-of-engagement scope (architecture.md
    /// Sec 9). Replaces any prior profile; an empty profile is the unrestricted
    /// scope, so applying it reopens the engagement. The caller records the
    /// change in the engagement's audit trail.
    /// </summary>
    public void ApplyRoe(RoeProfile roe)
    {
        ArgumentNullException.ThrowIfNull(roe);
        Roe = roe;
    }
}
