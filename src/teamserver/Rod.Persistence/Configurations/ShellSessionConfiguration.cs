using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.ShellSessions;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the caught reverse-shell session (architecture.md
/// Sec 8, the shellcatch transport). The domain entity is the model -- the
/// same shape the implant session takes -- so the durable registry and the
/// in-memory one read identical objects. The listener reference rides as
/// its raw Guid (the inner ring's convention for listener linkage), and
/// the guess/status enums store as int by convention.
/// </summary>
internal sealed class ShellSessionConfiguration : IEntityTypeConfiguration<ShellSession>
{
    public void Configure(EntityTypeBuilder<ShellSession> builder)
    {
        builder.ToTable("shell_sessions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasConversion(IdConverters.ShellSessionId)
            .HasColumnName("shell_session_id");

        builder.Property(s => s.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(s => s.UpgradedImplantId)
            .HasConversion(IdConverters.ImplantId)
            .HasColumnName("upgraded_implant_id");
        builder.Property(s => s.ListenerId)
            .HasColumnName("listener_id");

        builder.Property(s => s.RemoteAddress).HasMaxLength(256).IsRequired();
        builder.Property(s => s.OpenedAt).HasColumnName("opened_at");
        builder.Property(s => s.LastInputAt).HasColumnName("last_input_at");
        builder.Property(s => s.LastOutputAt).HasColumnName("last_output_at");
        builder.Property(s => s.EndedAt).HasColumnName("ended_at");
    }
}
