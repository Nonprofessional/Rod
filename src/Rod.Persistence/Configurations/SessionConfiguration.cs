using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Rod.CoreState.Sessions;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Session"/> (ADR 0003). Private parameterized
/// constructor binds the get-only scalars; the mutable fields
/// (<see cref="Session.Capabilities"/>, <see cref="Session.LastSeenAt"/>,
/// <see cref="Session.EndedAt"/>, <see cref="Session.Status"/>) carry private
/// setters EF writes through their backing fields.
/// <see cref="Session.Capabilities"/> is an <c>IReadOnlyList&lt;string&gt;</c>;
/// a value converter maps it to a Postgres <c>text[]</c> column (the Npgsql
/// provider stores the array natively, so the converter is a list-to-array
/// shim).
/// </summary>
internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasConversion(IdConverters.SessionId)
            .HasColumnName("session_id");

        builder.Property(s => s.ImplantId)
            .HasConversion(IdConverters.ImplantId)
            .HasColumnName("implant_id");
        builder.Property(s => s.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");

        // Capabilities: IReadOnlyList<string> <-> string[]. The Npgsql provider
        // maps string[] to text[] natively, so the converter only adapts the
        // collection shape; the column stores a real Postgres array. A custom
        // value comparer is needed because EF compares the CLR value for change
        // tracking, and List<string>'s default reference equality would mark
        // every touch as a change.
        var capConverter = new ValueConverter<IReadOnlyList<string>, string[]>(
            v => v.ToArray(),
            v => v.ToList());
        var capComparer = new ValueComparer<IReadOnlyList<string>>(
            (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
            v => v == null ? 0 : v.Aggregate(0, HashCode.Combine),
            v => v == null ? new List<string>() : v.ToList());

        builder.Property(s => s.Capabilities)
            .HasConversion(capConverter, capComparer)
            .HasColumnName("capabilities");
        builder.Property(s => s.StartedAt).HasColumnName("started_at");
        builder.Property(s => s.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(s => s.EndedAt).HasColumnName("ended_at");
        builder.Property(s => s.Status).HasColumnName("status");

        // Active-session presence reads (ListActiveAsync by engagement, and the
        // per-implant active lookup) are scoped; index both axes.
        builder.HasIndex(s => s.EngagementId).HasDatabaseName("ix_sessions_engagement_id");
        builder.HasIndex(s => s.ImplantId).HasDatabaseName("ix_sessions_implant_id");

        builder.UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
