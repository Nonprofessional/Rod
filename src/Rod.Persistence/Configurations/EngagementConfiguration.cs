using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Engagements;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Engagement"/> (ADR 0003). The aggregate has a
/// private parameterized constructor and get-only scalar properties, and exposes
/// its membership as a read-only list backed by the private
/// <c>_members</c> field. EF Core binds the scalars via the private constructor
/// (parameter names match the properties) and maps the membership as an owned
/// collection written through the <c>_members</c> field.
/// </summary>
internal sealed class EngagementConfiguration : IEntityTypeConfiguration<Engagement>
{
    public void Configure(EntityTypeBuilder<Engagement> builder)
    {
        builder.ToTable("engagements");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");

        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(512).IsRequired();
        builder.Property(e => e.OwnerId)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("owner_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        // The owner column is a denormalized view of the Owner-role member for
        // fast scoping queries; membership remains the source of truth and the
        // aggregate keeps its one-owner invariant regardless of this column.

        // Membership is an owned collection written through the private _members
        // field (the Members property is IReadOnlyList with no setter). The owned
        // row carries the engagement id as a shadow foreign key -- the membership
        // value object does not declare it, so the linkage lives only in the
        // store, never on the domain type.
        builder.OwnsMany(e => e.Members, mb =>
        {
            mb.ToTable("engagement_members");

            // Shadow engagement-id foreign key: the owned row's link to its
            // aggregate, with the same uuid conversion as the aggregate's key.
            mb.Property<EngagementId>("EngagementId")
                .HasConversion(IdConverters.EngagementId)
                .HasColumnName("engagement_id");

            mb.WithOwner().HasForeignKey("EngagementId");

            // Composite key within the engagement: an operator is a member at
            // most once, so (engagement, operator) is the natural identifier.
            mb.HasKey("EngagementId", nameof(EngagementMembership.OperatorId));

            mb.Property(m => m.OperatorId)
                .HasConversion(IdConverters.OperatorId)
                .HasColumnName("operator_id");
            // Role is an int column -- the audit canonical form already hashes
            // (int)Kind, so int storage keeps the domain and the store aligned.
            mb.Property(m => m.Role).HasColumnName("role");
            mb.Property(m => m.AddedAt).HasColumnName("added_at");

            // AddedAt has no setter at all; the internal parameterized
            // constructor sets it, so field access lets EF write on reload.
            mb.UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        // Field access so the read-only Members navigation and the private
        // setters on the scalars are read/written through their backing fields,
        // not via property setters the domain deliberately hides.
        builder.Navigation(e => e.Members)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
