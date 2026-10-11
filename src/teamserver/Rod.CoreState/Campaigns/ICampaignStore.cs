using Rod.CoreState.Deployment;

namespace Rod.CoreState.Campaigns;

/// <summary>
/// The resolved lure behind one unguessable lure id: everything the serving
/// edge needs (architecture.md Sec 11.5) -- the scope the socket checks,
/// the artifact the link serves (null until the build completes), and the
/// evidence stamps' target. A bare resolution shape, not the row: the
/// public edge never holds the campaign aggregate.
/// </summary>
public sealed record CampaignLure(
    CampaignId CampaignId,
    EngagementId EngagementId,
    CampaignRecipientId RecipientId,
    string CampaignName,
    string RecipientEmail,
    Guid? PayloadId);

/// <summary>
/// The attribution an enrollment resolves when the redeemed token was
/// baked for a campaign recipient (architecture.md Sec 11.5): the ids that
/// stamp the implant row, and the words the audit fact reads.
/// </summary>
public sealed record CampaignAttribution(
    CampaignId CampaignId,
    CampaignRecipientId RecipientId,
    string CampaignName,
    string RecipientEmail);

/// <summary>
/// The durable home for delivery campaigns (architecture.md Sec 11.5):
/// engagement-scoped rows like launchers and webhook subscriptions,
/// in-memory by default and Postgres-backed when the connection string is
/// set. Reads resolve whole campaigns (the operator surface) and two
/// public-edge lookups (by lure id, by baked token id); every mutation is
/// a targeted, guard-carrying operation rather than a whole-row write, so
/// the engine's recipient arcs never race the public edge's evidence
/// stamps -- the same reason the deploy-token store's redeem is a
/// conditional update rather than a read-modify-write.
/// </summary>
public interface ICampaignStore
{
    /// <summary>Stores a campaign with its recipients (creation; the row is
    /// upserted whole).</summary>
    Task SaveAsync(Campaign campaign, CancellationToken cancellationToken = default);

    /// <summary>The campaign, or null when unknown.</summary>
    Task<Campaign?> FindAsync(CampaignId id, CancellationToken cancellationToken = default);

    /// <summary>The campaign, or null when unknown or outside the
    /// engagement.</summary>
    Task<Campaign?> FindAsync(CampaignId id, EngagementId engagementId, CancellationToken cancellationToken = default);

    /// <summary>The engagement's campaigns, newest first.</summary>
    Task<IReadOnlyList<Campaign>> ListByEngagementAsync(EngagementId engagementId, CancellationToken cancellationToken = default);

    /// <summary>Every campaign still in the engine's care, across
    /// engagements -- the send engine's scan.</summary>
    Task<IReadOnlyList<Campaign>> ListLaunchedAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves a lure by its unguessable id, or null when no
    /// recipient carries it.</summary>
    Task<CampaignLure?> FindByLureAsync(Guid lureId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the recipient a baked enrollment credential binds to, or
    /// null when the token never rode a campaign -- the ordinary build
    /// path, which this lookup answers as unattributed.
    /// </summary>
    Task<CampaignAttribution?> FindByEnrollTokenAsync(DeployTokenId tokenId, CancellationToken cancellationToken = default);

    /// <summary>Arms a Draft campaign; false when it is not Draft (the
    /// second launch answers 409, not a second arc).</summary>
    Task<bool> LaunchAsync(CampaignId id, EngagementId engagementId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Revokes from any non-revoked state; false when already
    /// revoked.</summary>
    Task<bool> RevokeAsync(CampaignId id, EngagementId engagementId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>
    /// Binds the minted credential and the submitted job, moving the
    /// recipient to Building. Valid from Pending or Building (a re-
    /// submission after a lost job, the fresh token overwriting the stale
    /// binding).
    /// </summary>
    Task<bool> NoteBuildingAsync(CampaignId campaign, CampaignRecipientId recipient, DeployTokenId enrollToken, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Records the relay's acceptance with the artifact the lure
    /// serves; valid from Building only.</summary>
    Task<bool> NoteSentAsync(CampaignId campaign, CampaignRecipientId recipient, Guid payloadId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Records a terminal failure from any non-terminal point of
    /// the arc.</summary>
    Task<bool> NoteFailedAsync(CampaignId campaign, CampaignRecipientId recipient, string reason, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>The first open stamp; monotonic, false when already
    /// stamped.</summary>
    Task<bool> NoteOpenedAsync(CampaignId campaign, CampaignRecipientId recipient, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>The first click stamp; monotonic.</summary>
    Task<bool> NoteClickedAsync(CampaignId campaign, CampaignRecipientId recipient, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>The executed binding: the implant that redeemed the
    /// recipient's credential, stamped once.</summary>
    Task<bool> NoteExecutedAsync(CampaignId campaign, CampaignRecipientId recipient, Guid implantId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a launched campaign to Completed when no recipient is mid-arc;
    /// false when one still is, or the campaign is not launched. The
    /// engine offers it after every recipient transition.
    /// </summary>
    Task<bool> TryCompleteAsync(CampaignId campaign, DateTimeOffset at, CancellationToken cancellationToken = default);
}
