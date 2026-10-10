using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Engagements;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for engagement memberships (ADR 0003, architecture.md
/// Sec 3). One row per granted membership: the engagement, the operator, the
/// role, and when the grant was made. The owner holds no row -- ownership is
/// the engagement's own field, bound once at creation -- and the table's
/// name deliberately echoes the <c>engagement_members</c> table an earlier
/// model carried (it was dropped with that model; the shape returns because
/// the membership decision returned, with a two-role set this time).
/// </summary>
internal sealed class StoredEngagementMembership
{
    public EngagementId EngagementId { get; set; }
    public OperatorId OperatorId { get; set; }
    public EngagementRole Role { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

internal sealed class StoredEngagementMembershipConfiguration
    : IEntityTypeConfiguration<StoredEngagementMembership>
{
    public void Configure(EntityTypeBuilder<StoredEngagementMembership> builder)
    {
        builder.ToTable("engagement_members");

        builder.HasKey(m => new { m.EngagementId, m.OperatorId });

        builder.Property(m => m.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(m => m.OperatorId)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("operator_id");
        builder.Property(m => m.Role).HasConversion<int>().HasColumnName("role");
        builder.Property(m => m.AddedAt).HasColumnName("added_at");

        // The engagement's member roster reads by engagement; an operator's
        // membership index (the listing filter) reads by operator.
        builder.HasIndex(m => m.OperatorId).HasDatabaseName("ix_engagement_members_operator_id");
    }
}
