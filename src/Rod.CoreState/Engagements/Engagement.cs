namespace Rod.CoreState.Engagements;

/// <summary>
/// An engagement -- the unit of tenancy, isolation, authorization, and evidence
/// for one authorized operation (architecture.md Sec 3). Aggregate root: all
/// membership mutation goes through it so the invariants hold by construction.
/// </summary>
public sealed class Engagement
{
    private readonly List<EngagementMembership> _members = new();

    public EngagementId Id { get; }
    public string Name { get; }
    public OperatorId OwnerId { get; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<EngagementMembership> Members => _members;

    private Engagement(EngagementId id, string name, OperatorId ownerId, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        OwnerId = ownerId;
        CreatedAt = createdAt;
    }

    /// <summary>
    /// Creates a new engagement and registers the creating operator as its
    /// single <see cref="Role.Owner"/> member. The engagement always starts with
    /// exactly one owner; the owner can never be removed (see <see cref="RemoveMember"/>).
    /// </summary>
    public static Engagement Create(
        EngagementId id,
        string name,
        OperatorId ownerId,
        DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Engagement name is required.", nameof(name));

        var engagement = new Engagement(id, name.Trim(), ownerId, createdAt);
        engagement._members.Add(new EngagementMembership(ownerId, Role.Owner, createdAt));
        return engagement;
    }

    /// <summary>
    /// Adds an operator as a member. <see cref="Role.Owner"/> is refused: an
    /// engagement has exactly one owner, set at creation.
    /// </summary>
    public EngagementMembership AddMember(OperatorId operatorId, Role role, DateTimeOffset at)
    {
        if (operatorId == OwnerId)
            throw new EngagementDomainException(
                "The engagement owner is already a member.");
        if (role == Role.Owner)
            throw new EngagementDomainException(
                "An engagement has exactly one owner, set at creation.");
        if (_members.Any(m => m.OperatorId == operatorId))
            throw new EngagementDomainException(
                $"Operator {operatorId} is already a member of engagement {Id}.");

        var membership = new EngagementMembership(operatorId, role, at);
        _members.Add(membership);
        return membership;
    }

    /// <summary>Changes a member's role. The owner's role is fixed.</summary>
    public EngagementMembership ChangeMemberRole(OperatorId operatorId, Role role, DateTimeOffset at)
    {
        if (operatorId == OwnerId)
            throw new EngagementDomainException(
                "The engagement owner's role cannot be changed.");
        if (role == Role.Owner)
            throw new EngagementDomainException(
                "An engagement has exactly one owner, set at creation.");

        var membership = _members.FirstOrDefault(m => m.OperatorId == operatorId)
            ?? throw new EngagementDomainException(
                $"Operator {operatorId} is not a member of engagement {Id}.");

        membership.Role = role;
        return membership;
    }

    /// <summary>Removes a member. The owner can never be removed.</summary>
    public void RemoveMember(OperatorId operatorId)
    {
        if (operatorId == OwnerId)
            throw new EngagementDomainException(
                "The engagement owner cannot be removed; an engagement always has exactly one owner.");

        var membership = _members.FirstOrDefault(m => m.OperatorId == operatorId)
            ?? throw new EngagementDomainException(
                $"Operator {operatorId} is not a member of engagement {Id}.");

        _members.Remove(membership);
    }

    public bool HasMember(OperatorId operatorId)
        => _members.Any(m => m.OperatorId == operatorId);

    public Role? RoleOf(OperatorId operatorId)
        => _members.FirstOrDefault(m => m.OperatorId == operatorId)?.Role;
}
