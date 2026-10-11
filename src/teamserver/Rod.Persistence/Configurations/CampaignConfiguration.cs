using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Campaigns;
using Rod.CoreState.Deployment;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for delivery campaigns (architecture.md Sec 11.5): one
/// row per campaign plus one row per recipient under it. The recipient
/// rows carry the attribution binding (the baked token id) and the
/// evidence stamps the public edge writes, which is why they are a table
/// of their own rather than a JSON column -- the enroll lookup and the
/// lure resolution are indexed single-row reads, not aggregate
/// deserializations. A Persistence-owned mirror of core state's
/// <see cref="Campaign"/> aggregate (the domain stays free of any stored
/// shape).
/// </summary>
internal sealed class StoredCampaign
{
    public Guid Id { get; set; }
    public EngagementId EngagementId { get; set; }
    public string Name { get; set; } = string.Empty;
    public OperatorId CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int State { get; set; }
    public string RelayHost { get; set; } = string.Empty;
    public int RelayPort { get; set; }
    public int RelayTls { get; set; }
    public string? RelayUsername { get; set; }
    public string? RelayPassword { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool BodyIsHtml { get; set; }
    public string BuildRequestJson { get; set; } = string.Empty;
    public Guid ListenerId { get; set; }
    public DateTimeOffset? LaunchedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public List<StoredCampaignRecipient> Recipients { get; set; } = [];

    public static StoredCampaign From(Campaign campaign) => new()
    {
        Id = campaign.Id.Value,
        EngagementId = campaign.EngagementId,
        Name = campaign.Name,
        CreatedBy = campaign.CreatedBy,
        CreatedAt = campaign.CreatedAt,
        State = (int)campaign.State,
        RelayHost = campaign.RelayHost,
        RelayPort = campaign.RelayPort,
        RelayTls = (int)campaign.RelayTls,
        RelayUsername = campaign.RelayUsername,
        RelayPassword = campaign.RelayPassword,
        FromAddress = campaign.FromAddress,
        Subject = campaign.Subject,
        Body = campaign.Body,
        BodyIsHtml = campaign.BodyIsHtml,
        BuildRequestJson = campaign.BuildRequestJson,
        ListenerId = campaign.ListenerId,
        LaunchedAt = campaign.LaunchedAt,
        CompletedAt = campaign.CompletedAt,
        RevokedAt = campaign.RevokedAt,
        Recipients = campaign.Recipients.Select(StoredCampaignRecipient.From).ToList(),
    };

    public Campaign ToDomain() => new(
        new CampaignId(Id),
        EngagementId,
        Name,
        CreatedBy,
        CreatedAt,
        RelayHost,
        RelayPort,
        (CampaignRelayTls)RelayTls,
        RelayUsername,
        RelayPassword,
        FromAddress,
        Subject,
        Body,
        BodyIsHtml,
        BuildRequestJson,
        ListenerId,
        Recipients.Select(r => r.ToDomain()).ToList(),
        (CampaignState)State,
        LaunchedAt,
        CompletedAt,
        RevokedAt);
}

/// <summary>
/// One recipient row. The status/failure/evidence columns are written by
/// the store's targeted operations, never by re-saving the aggregate --
/// the recipient table is the concurrent edge of the campaign, and the
/// targeted updates are what keep the engine's arcs from racing the
/// public edge's stamps.
/// </summary>
internal sealed class StoredCampaignRecipient
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public StoredCampaign? Campaign { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Name { get; set; }
    public Guid LureId { get; set; }
    public int Status { get; set; }
    public string? Failure { get; set; }
    public Guid? JobId { get; set; }
    public Guid? EnrollTokenId { get; set; }
    public Guid? PayloadId { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset? ClickedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public Guid? EnrolledImplantId { get; set; }

    public static StoredCampaignRecipient From(CampaignRecipient recipient) => new()
    {
        Id = recipient.Id.Value,
        Email = recipient.Email,
        Name = recipient.Name,
        LureId = recipient.LureId,
        Status = (int)recipient.Status,
        Failure = recipient.Failure,
        JobId = recipient.JobId,
        EnrollTokenId = recipient.EnrollTokenId?.Value,
        PayloadId = recipient.PayloadId,
        SentAt = recipient.SentAt,
        OpenedAt = recipient.OpenedAt,
        ClickedAt = recipient.ClickedAt,
        ExecutedAt = recipient.ExecutedAt,
        EnrolledImplantId = recipient.EnrolledImplantId,
    };

    public CampaignRecipient ToDomain() => new(
        new CampaignRecipientId(Id),
        Email,
        Name,
        LureId,
        (CampaignRecipientStatus)Status,
        Failure,
        JobId,
        EnrollTokenId is { } token ? new DeployTokenId(token) : null,
        PayloadId,
        SentAt,
        OpenedAt,
        ClickedAt,
        ExecutedAt,
        EnrolledImplantId);
}

internal sealed class CampaignConfiguration : IEntityTypeConfiguration<StoredCampaign>
{
    public void Configure(EntityTypeBuilder<StoredCampaign> builder)
    {
        builder.ToTable("campaigns");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("campaign_id");

        builder.Property(c => c.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(CampaignLimits.MaxNameBytes);
        builder.Property(c => c.CreatedBy)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("created_by");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.State).HasConversion<int>().HasColumnName("state");
        builder.Property(c => c.RelayHost).HasColumnName("relay_host").HasMaxLength(255);
        builder.Property(c => c.RelayPort).HasColumnName("relay_port");
        builder.Property(c => c.RelayTls).HasConversion<int>().HasColumnName("relay_tls");
        builder.Property(c => c.RelayUsername).HasColumnName("relay_username").HasMaxLength(255);
        builder.Property(c => c.RelayPassword).HasColumnName("relay_password").HasMaxLength(255);
        builder.Property(c => c.FromAddress).HasColumnName("from_address").HasMaxLength(CampaignLimits.MaxAddressBytes);
        builder.Property(c => c.Subject).HasColumnName("subject").HasMaxLength(CampaignLimits.MaxSubjectBytes);
        builder.Property(c => c.Body).HasColumnName("body").HasMaxLength(CampaignLimits.MaxBodyBytes);
        builder.Property(c => c.BodyIsHtml).HasColumnName("body_is_html");
        builder.Property(c => c.BuildRequestJson).HasColumnName("build_request_json").HasMaxLength(CampaignLimits.MaxBuildRequestBytes);
        builder.Property(c => c.ListenerId).HasColumnName("listener_id");
        builder.Property(c => c.LaunchedAt).HasColumnName("launched_at");
        builder.Property(c => c.CompletedAt).HasColumnName("completed_at");
        builder.Property(c => c.RevokedAt).HasColumnName("revoked_at");

        // The operator API lists by engagement; the send engine scans the
        // launched set across engagements.
        builder.HasIndex(c => c.EngagementId).HasDatabaseName("ix_campaigns_engagement_id");
        builder.HasIndex(c => c.State).HasDatabaseName("ix_campaigns_state");

        builder.HasMany(c => c.Recipients)
            .WithOne(r => r.Campaign)
            .HasForeignKey(r => r.CampaignId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CampaignRecipientConfiguration : IEntityTypeConfiguration<StoredCampaignRecipient>
{
    public void Configure(EntityTypeBuilder<StoredCampaignRecipient> builder)
    {
        builder.ToTable("campaign_recipients");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("campaign_recipient_id");

        builder.Property(r => r.CampaignId).HasColumnName("campaign_id");
        builder.Property(r => r.Email).HasColumnName("email").HasMaxLength(CampaignLimits.MaxAddressBytes);
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(CampaignLimits.MaxAddressBytes);
        builder.Property(r => r.LureId).HasColumnName("lure_id");
        builder.Property(r => r.Status).HasConversion<int>().HasColumnName("status");
        builder.Property(r => r.Failure).HasColumnName("failure").HasMaxLength(CampaignLimits.MaxFailureBytes);
        builder.Property(r => r.JobId).HasColumnName("job_id");
        builder.Property(r => r.EnrollTokenId).HasColumnName("enroll_token_id");
        builder.Property(r => r.PayloadId).HasColumnName("payload_id");
        builder.Property(r => r.SentAt).HasColumnName("sent_at");
        builder.Property(r => r.OpenedAt).HasColumnName("opened_at");
        builder.Property(r => r.ClickedAt).HasColumnName("clicked_at");
        builder.Property(r => r.ExecutedAt).HasColumnName("executed_at");
        builder.Property(r => r.EnrolledImplantId).HasColumnName("enrolled_implant_id");

        // The public edge resolves a lure by its unguessable id alone; the
        // enrollment path resolves attribution by the baked token. Both are
        // single-row reads, and both ids are unique by construction.
        builder.HasIndex(r => r.LureId).IsUnique().HasDatabaseName("ux_campaign_recipients_lure_id");
        builder.HasIndex(r => r.EnrollTokenId).IsUnique()
            .HasDatabaseName("ux_campaign_recipients_enroll_token_id")
            // A partial index: rows before the engine's mint (and recipients
            // of a Draft campaign) carry no token, and a full unique index
            // would admit only one NULL under the default b-tree semantics.
            .HasFilter("enroll_token_id IS NOT NULL");
    }
}
