namespace Rod.CoreState.Engagements;

/// <summary>
/// Resolves an operator's standing toward an engagement (architecture.md
/// Sec 3): the owner check against the engagement's own field, then the
/// membership check against the store -- one lookup either way. This is the
/// single place the access rule lives; the transport and operator layers
/// build their enforcement (route filters, endpoint checks) on it, so the
/// rule cannot drift between them. The resolver is stateless and reads both
/// stores fresh on every call -- a role change or removal takes effect at
/// the very next request, the same freshness the session validation keeps
/// for operator account state.
/// </summary>
public sealed class EngagementAccessResolver
{
    private readonly IEngagementRepository _engagements;
    private readonly IEngagementMembershipStore _memberships;

    public EngagementAccessResolver(
        IEngagementRepository engagements,
        IEngagementMembershipStore memberships)
    {
        _engagements = engagements;
        _memberships = memberships;
    }

    public async Task<EngagementAccess> ResolveAsync(
        EngagementId engagementId,
        OperatorId operatorId,
        CancellationToken cancellationToken = default)
    {
        var engagement = await _engagements.FindAsync(engagementId, cancellationToken);
        if (engagement is null)
            return EngagementAccess.Missing;

        if (engagement.OwnerId == operatorId)
            return new EngagementAccess(true, EngagementAccessLevel.Owner, engagement.OwnerId);

        var membership = await _memberships.FindAsync(engagementId, operatorId, cancellationToken);
        if (membership is null)
            return new EngagementAccess(true, EngagementAccessLevel.None, engagement.OwnerId);

        return new EngagementAccess(
            true,
            membership.Role == EngagementRole.Writer
                ? EngagementAccessLevel.Writer
                : EngagementAccessLevel.Reader,
            engagement.OwnerId);
    }
}
