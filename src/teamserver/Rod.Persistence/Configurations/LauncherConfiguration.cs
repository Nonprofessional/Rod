using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState.Launchers;
using Rod.Persistence;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Launcher"/> (ADR 0003): a rendered
/// launcher row with its re-copyable download credential. Private setter
/// (<see cref="Launcher.Revoke"/>) writes via its backing field. The typed
/// ids map to Postgres <c>uuid</c>; the secret is plain text by design --
/// the row is the one clear copy behind the operator surface, deletable with
/// itself (the token store keeps only its hash).
/// </summary>
internal sealed class LauncherConfiguration : IEntityTypeConfiguration<Launcher>
{
    public void Configure(EntityTypeBuilder<Launcher> builder)
    {
        builder.ToTable("launchers");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id)
            .HasConversion(IdConverters.LauncherId)
            .HasColumnName("launcher_id");

        builder.Property(l => l.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(l => l.PayloadId).HasColumnName("payload_id");
        // The payload's target OS at render time -- the snapshot that keeps
        // a row's re-rendered one-liners filtered to its payload's families
        // after the payload leaves the library. Nullable: rows and payloads
        // that predate the target field carry no OS.
        builder.Property(l => l.PayloadOs).HasColumnName("payload_os");
        builder.Property(l => l.ListenerId).HasColumnName("listener_id");
        builder.Property(l => l.FrontName).HasColumnName("front_name");
        builder.Property(l => l.FrontEndpoint).HasColumnName("front_endpoint");
        builder.Property(l => l.TokenId)
            .HasConversion(IdConverters.DeployTokenId)
            .HasColumnName("token_id");
        builder.Property(l => l.TokenSecret).HasColumnName("token_secret");
        builder.Property(l => l.Url).HasColumnName("url");
        builder.Property(l => l.MaxUses).HasColumnName("max_uses");
        builder.Property(l => l.ExpiresAt).HasColumnName("expires_at");
        builder.Property(l => l.CreatedAt).HasColumnName("created_at");
        builder.Property(l => l.CreatedBy)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("created_by");
        builder.Property(l => l.RevokedAt).HasColumnName("revoked_at");

        // Engagement scoping is structural: the listing reads one
        // engagement's rows as a cheap scoped query.
        builder.HasIndex(l => l.EngagementId).HasDatabaseName("ix_launchers_engagement_id");

        builder.UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
