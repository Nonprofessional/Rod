using Rod.CoreState.Deployment;

namespace Rod.CoreState.Campaigns;

/// <summary>
/// In-memory <see cref="ICampaignStore"/>: the process-lifetime default
/// behind the port, swapped for the Postgres twin at the composition root
/// when the connection string is set. Every mutation routes through the
/// entity's own guard-carrying methods under one lock, so the in-memory
/// semantics and the durable twin's WHERE clauses say the same thing.
/// </summary>
public sealed class InMemoryCampaignStore : ICampaignStore
{
    private readonly object _lock = new();
    private readonly Dictionary<CampaignId, Campaign> _campaigns = new();

    public Task SaveAsync(Campaign campaign, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _campaigns[campaign.Id] = campaign;
        }
        return Task.CompletedTask;
    }

    public Task<Campaign?> FindAsync(CampaignId id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_campaigns.TryGetValue(id, out var campaign) ? campaign : null);
        }
    }

    public Task<Campaign?> FindAsync(CampaignId id, EngagementId engagementId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(
                _campaigns.TryGetValue(id, out var campaign) && campaign.EngagementId == engagementId
                    ? campaign
                    : null);
        }
    }

    public Task<IReadOnlyList<Campaign>> ListByEngagementAsync(EngagementId engagementId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Campaign>>(
                _campaigns.Values
                    .Where(c => c.EngagementId == engagementId)
                    .OrderByDescending(c => c.CreatedAt)
                    .ToArray());
        }
    }

    public Task<IReadOnlyList<Campaign>> ListLaunchedAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Campaign>>(
                _campaigns.Values.Where(c => c.State == CampaignState.Launched).ToArray());
        }
    }

    public Task<CampaignLure?> FindByLureAsync(Guid lureId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            foreach (var campaign in _campaigns.Values)
            {
                var recipient = campaign.Recipients.FirstOrDefault(r => r.LureId == lureId);
                if (recipient is not null)
                {
                    return Task.FromResult<CampaignLure?>(new CampaignLure(
                        campaign.Id, campaign.EngagementId, recipient.Id,
                        campaign.Name, recipient.Email, recipient.PayloadId));
                }
            }
            return Task.FromResult<CampaignLure?>(null);
        }
    }

    public Task<CampaignAttribution?> FindByEnrollTokenAsync(DeployTokenId tokenId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            foreach (var campaign in _campaigns.Values)
            {
                var recipient = campaign.Recipients.FirstOrDefault(r => r.EnrollTokenId == tokenId);
                if (recipient is not null)
                {
                    return Task.FromResult<CampaignAttribution?>(new CampaignAttribution(
                        campaign.Id, recipient.Id, campaign.Name, recipient.Email));
                }
            }
            return Task.FromResult<CampaignAttribution?>(null);
        }
    }

    public Task<bool> LaunchAsync(CampaignId id, EngagementId engagementId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var campaign = Resolve(id, engagementId);
            return Task.FromResult(campaign?.Launch(at) ?? false);
        }
    }

    public Task<bool> RevokeAsync(CampaignId id, EngagementId engagementId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var campaign = Resolve(id, engagementId);
            return Task.FromResult(campaign?.Revoke(at) ?? false);
        }
    }

    public Task<bool> NoteBuildingAsync(CampaignId campaignId, CampaignRecipientId recipientId, DeployTokenId enrollToken, Guid jobId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var recipient = ResolveRecipient(campaignId, recipientId);
            return Task.FromResult(recipient?.MarkBuilding(enrollToken, jobId) ?? false);
        }
    }

    public Task<bool> NoteSentAsync(CampaignId campaignId, CampaignRecipientId recipientId, Guid payloadId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var recipient = ResolveRecipient(campaignId, recipientId);
            return Task.FromResult(recipient?.MarkSent(payloadId, at) ?? false);
        }
    }

    public Task<bool> NoteFailedAsync(CampaignId campaignId, CampaignRecipientId recipientId, string reason, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var recipient = ResolveRecipient(campaignId, recipientId);
            return Task.FromResult(recipient?.MarkFailed(reason, at) ?? false);
        }
    }

    public Task<bool> NoteOpenedAsync(CampaignId campaignId, CampaignRecipientId recipientId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var recipient = ResolveRecipient(campaignId, recipientId);
            return Task.FromResult(recipient?.NoteOpened(at) ?? false);
        }
    }

    public Task<bool> NoteClickedAsync(CampaignId campaignId, CampaignRecipientId recipientId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var recipient = ResolveRecipient(campaignId, recipientId);
            return Task.FromResult(recipient?.NoteClicked(at) ?? false);
        }
    }

    public Task<bool> NoteExecutedAsync(CampaignId campaignId, CampaignRecipientId recipientId, Guid implantId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var recipient = ResolveRecipient(campaignId, recipientId);
            return Task.FromResult(recipient?.NoteExecuted(implantId, at) ?? false);
        }
    }

    public Task<bool> TryCompleteAsync(CampaignId id, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_campaigns.TryGetValue(id, out var campaign) && campaign.TryComplete(at));
        }
    }

    private Campaign? Resolve(CampaignId id, EngagementId engagementId)
        => _campaigns.TryGetValue(id, out var campaign) && campaign.EngagementId == engagementId
            ? campaign
            : null;

    private CampaignRecipient? ResolveRecipient(CampaignId campaignId, CampaignRecipientId recipientId)
        => _campaigns.TryGetValue(campaignId, out var campaign)
            ? campaign.FindRecipient(recipientId)
            : null;
}
