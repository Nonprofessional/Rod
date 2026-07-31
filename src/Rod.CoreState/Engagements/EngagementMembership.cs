namespace Rod.CoreState.Engagements;

/// <summary>
/// An operator's membership in an engagement, binding them to a <see cref="Role"/>.
/// A value object owned by the <see cref="Engagement"/> aggregate: it is created
/// and mutated only through the aggregate so the invariants (one owner, no
/// duplicate members) stay centralized there.
/// </summary>
public sealed class EngagementMembership
{
    public OperatorId OperatorId { get; internal set; }
    public Role Role { get; internal set; }
    public DateTimeOffset AddedAt { get; }

    internal EngagementMembership(OperatorId operatorId, Role role, DateTimeOffset addedAt)
    {
        OperatorId = operatorId;
        Role = role;
        AddedAt = addedAt;
    }

    public override string ToString()
        => $"{OperatorId} as {Role}";
}
