namespace Rod.CoreState.Engagements;

/// <summary>
/// One operator's standing in one engagement (architecture.md Sec 3): the
/// role the engagement's owner granted, recorded when it was granted. The
/// owner holds no membership row -- ownership is the engagement's own field,
/// immutable from creation, and carries member management beside full write
/// access; a membership is the granted tier beneath it.
/// </summary>
public sealed record EngagementMembership(
    EngagementId EngagementId,
    OperatorId OperatorId,
    EngagementRole Role,
    DateTimeOffset AddedAt);
