using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Listeners;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the engagement-scoped listener definitions (ADR 0003).
/// One row per runtime-created listener: the engagement it answers for, the
/// transport's wire name, the bind address the teamserver re-opens on restart,
/// and the public endpoint implants dial. The durable analogue of core state's
/// <see cref="ListenerDefinition"/> record; it lives here so the domain stays
/// free of any stored shape.
/// </summary>
internal sealed class StoredListenerDefinition
{
    public Guid Id { get; set; }
    public EngagementId EngagementId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Transport { get; set; } = string.Empty;
    public string BindAddress { get; set; } = string.Empty;
    public string PublicEndpoint { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RepointedAt { get; set; }
}

internal sealed class StoredListenerDefinitionConfiguration : IEntityTypeConfiguration<StoredListenerDefinition>
{
    public void Configure(EntityTypeBuilder<StoredListenerDefinition> builder)
    {
        builder.ToTable("listener_definitions");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("listener_id");

        builder.Property(l => l.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(l => l.Name).HasColumnName("name").HasMaxLength(200);
        builder.Property(l => l.Transport).HasColumnName("transport").HasMaxLength(32);
        builder.Property(l => l.BindAddress).HasColumnName("bind_address").HasMaxLength(200);
        builder.Property(l => l.PublicEndpoint).HasColumnName("public_endpoint").HasMaxLength(500);
        builder.Property(l => l.CreatedAt).HasColumnName("created_at");
        builder.Property(l => l.RepointedAt).HasColumnName("repointed_at");

        // The engagement's listener listing reads by engagement; the port-
        // uniqueness pre-check reads them all.
        builder.HasIndex(l => l.EngagementId).HasDatabaseName("ix_listener_definitions_engagement_id");
    }
}
