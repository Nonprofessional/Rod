using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.WebShells;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the web-shell endpoint profile (architecture.md
/// Sec 5.2's Web-shell class): the connection facts of a placed script,
/// keyed by the WebShell-class implant row it hangs off. The probe stamps
/// have private setters the entity's own NoteProbe drives; EF writes them
/// through the same properties, so the durable pair and the in-memory one
/// mutate one shape.
/// </summary>
internal sealed class WebShellProfileConfiguration : IEntityTypeConfiguration<WebShellProfile>
{
    public void Configure(EntityTypeBuilder<WebShellProfile> builder)
    {
        builder.ToTable("webshell_profiles");

        builder.HasKey(p => p.ImplantId);
        builder.Property(p => p.ImplantId)
            .HasConversion(IdConverters.ImplantId)
            .HasColumnName("implant_id");

        builder.Property(p => p.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(p => p.RegisteredBy)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("registered_by");

        builder.Property(p => p.Url).HasMaxLength(2048).IsRequired();
        builder.Property(p => p.AdapterId).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Password).HasMaxLength(256).IsRequired();
        builder.Property(p => p.Encoder).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Decoder).HasMaxLength(64).IsRequired();
        builder.Property(p => p.RegisteredAt).HasColumnName("registered_at");
        builder.Property(p => p.LastProbeAt).HasColumnName("last_probe_at");
        builder.Property(p => p.LastProbeOk).HasColumnName("last_probe_ok");
    }
}
