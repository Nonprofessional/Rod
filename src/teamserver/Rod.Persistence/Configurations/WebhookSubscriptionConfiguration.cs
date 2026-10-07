using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Webhooks;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the engagement-scoped webhook subscriptions
/// (architecture.md Sec 4.4). One row per subscription with its target and
/// notifiable-kind selection flattened onto columns -- the delivery
/// bookkeeping among them, which is what lets a registered channel survive
/// a teamserver restart with its history intact. A Persistence-owned
/// mirror of core state's <see cref="WebhookSubscription"/> aggregate (the
/// domain stays free of any stored shape); the kind selection becomes a
/// text array of live-event kind names.
/// </summary>
internal sealed class StoredWebhookSubscription
{
    public Guid Id { get; set; }
    public EngagementId EngagementId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public List<string> EventKinds { get; set; } = [];
    public bool Enabled { get; set; }
    public int DeliveryCount { get; set; }
    public DateTimeOffset? LastDeliveredAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastFailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public OperatorId CreatedBy { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }

    public static StoredWebhookSubscription From(WebhookSubscription subscription) => new()
    {
        Id = subscription.Id.Value,
        EngagementId = subscription.EngagementId,
        Name = subscription.Name,
        Url = subscription.Url,
        EventKinds = subscription.EventKinds.Select(k => k.ToString()).ToList(),
        Enabled = subscription.Enabled,
        DeliveryCount = subscription.DeliveryCount,
        LastDeliveredAt = subscription.LastDeliveredAt,
        ConsecutiveFailures = subscription.ConsecutiveFailures,
        LastFailureReason = subscription.LastFailureReason,
        CreatedAt = subscription.CreatedAt,
        CreatedBy = subscription.CreatedBy,
        DisabledAt = subscription.DisabledAt,
    };

    public WebhookSubscription ToDomain() => new(
        new WebhookSubscriptionId(Id),
        EngagementId,
        Name,
        Url,
        EventKinds.Select(k => Enum.Parse<LiveEventKind>(k)).ToArray(),
        Enabled,
        DeliveryCount,
        LastDeliveredAt,
        ConsecutiveFailures,
        LastFailureReason,
        CreatedAt,
        CreatedBy,
        DisabledAt);
}

internal sealed class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<StoredWebhookSubscription>
{
    public void Configure(EntityTypeBuilder<StoredWebhookSubscription> builder)
    {
        builder.ToTable("webhook_subscriptions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("webhook_subscription_id");

        builder.Property(s => s.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(s => s.Name).HasColumnName("name").HasMaxLength(200);
        builder.Property(s => s.Url).HasColumnName("url").HasMaxLength(2048);
        builder.Property(s => s.EventKinds).HasColumnName("event_kinds");
        builder.Property(s => s.Enabled).HasColumnName("enabled");
        builder.Property(s => s.DeliveryCount).HasColumnName("delivery_count");
        builder.Property(s => s.LastDeliveredAt).HasColumnName("last_delivered_at");
        builder.Property(s => s.ConsecutiveFailures).HasColumnName("consecutive_failures");
        builder.Property(s => s.LastFailureReason).HasColumnName("last_failure_reason").HasMaxLength(200);
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.CreatedBy)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("created_by");
        builder.Property(s => s.DisabledAt).HasColumnName("disabled_at");

        // The forwarder's tick scans enabled subscriptions across engagements
        // and the operator API lists by engagement; both read through this
        // index.
        builder.HasIndex(s => s.EngagementId).HasDatabaseName("ix_webhook_subscriptions_engagement_id");
        builder.HasIndex(s => s.Enabled).HasDatabaseName("ix_webhook_subscriptions_enabled");
    }
}
