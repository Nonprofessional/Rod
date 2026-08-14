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

    private Engagement(EngagementId id, string name, OperatorId ownerId, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        OwnerId = ownerId;
        CreatedAt = createdAt;
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
}
