using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Engagements;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Engagement"/> (ADR 0003). The aggregate has a
/// private parameterized constructor and get-only scalar properties; EF Core
/// binds them via the private constructor (parameter names match the properties).
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

        // The ROE scope is one JSON document column: the profile is a pair of
        // allow-lists the domain reads whole, never queries field-by-field, so
        // a value converter keeps the aggregate mapping scalar-simple. A null
        // column is the unrestricted scope (the record predates the profile).
        builder.Property(e => e.Roe)
            .HasColumnName("roe")
            .HasConversion(
                profile => RoeProfileConverters.ToJson(profile),
                json => RoeProfileConverters.FromJson(json));

        // Field access so the private setters on the scalars are read/written
        // through their backing fields, not via property setters the domain
        // deliberately hides.
        builder.UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>JSON round-trip for <see cref="RoeProfile"/> storage.</summary>
internal static class RoeProfileConverters
{
    private sealed record StoredRoe(IReadOnlyList<string>? PermittedVerbs, IReadOnlyList<string>? PermittedImplants);

    public static string ToJson(RoeProfile profile)
        => System.Text.Json.JsonSerializer.Serialize(
            new StoredRoe(profile.PermittedVerbs, profile.PermittedImplants));

    public static RoeProfile FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return RoeProfile.Unrestricted;
        var stored = System.Text.Json.JsonSerializer.Deserialize<StoredRoe>(json);
        return new RoeProfile(stored?.PermittedVerbs, stored?.PermittedImplants);
    }
}
