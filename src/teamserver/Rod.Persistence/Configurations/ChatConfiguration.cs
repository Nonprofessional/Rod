using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.CoreState.Operators;

namespace Rod.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the engagement's chat channels and messages
/// (architecture.md Sec 15.3, ADR-005). Persistence-owned mirrors of the
/// core-state aggregates; mutable process knowledge that never enters the
/// audit chain.
/// </summary>
internal sealed class StoredChatChannel
{
    public Guid Id { get; set; }
    public EngagementId EngagementId { get; set; }
    public string Name { get; set; } = string.Empty;
    public OperatorId CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static StoredChatChannel From(ChatChannel channel) => new()
    {
        Id = channel.Id.Value,
        EngagementId = channel.EngagementId,
        Name = channel.Name,
        CreatedBy = channel.CreatedBy,
        CreatedAt = channel.CreatedAt,
    };

    public ChatChannel ToDomain() => new(new ChatChannelId(Id), EngagementId, Name, CreatedBy, CreatedAt);
}

internal sealed class ChatChannelConfiguration : IEntityTypeConfiguration<StoredChatChannel>
{
    public void Configure(EntityTypeBuilder<StoredChatChannel> builder)
    {
        builder.ToTable("chat_channels");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("chat_channel_id");

        builder.Property(c => c.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(200);
        builder.Property(c => c.CreatedBy)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("created_by");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(c => c.EngagementId).HasDatabaseName("ix_chat_channels_engagement_id");
    }
}

internal sealed class StoredChatMessage
{
    public Guid Id { get; set; }
    public EngagementId EngagementId { get; set; }
    public Guid ChannelId { get; set; }
    public OperatorId AuthorId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }

    public static StoredChatMessage From(ChatMessage message) => new()
    {
        Id = message.Id.Value,
        EngagementId = message.EngagementId,
        ChannelId = message.ChannelId.Value,
        AuthorId = message.AuthorId,
        Body = message.Body,
        SentAt = message.SentAt,
    };

    public ChatMessage ToDomain() => new(
        new ChatMessageId(Id),
        EngagementId,
        new ChatChannelId(ChannelId),
        AuthorId,
        Body,
        SentAt);
}

internal sealed class ChatMessageConfiguration : IEntityTypeConfiguration<StoredChatMessage>
{
    public void Configure(EntityTypeBuilder<StoredChatMessage> builder)
    {
        builder.ToTable("chat_messages");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("chat_message_id");

        builder.Property(m => m.EngagementId)
            .HasConversion(IdConverters.EngagementId)
            .HasColumnName("engagement_id");
        builder.Property(m => m.ChannelId).HasColumnName("chat_channel_id");
        builder.Property(m => m.AuthorId)
            .HasConversion(IdConverters.OperatorId)
            .HasColumnName("author_id");
        builder.Property(m => m.Body).HasColumnName("body");
        builder.Property(m => m.SentAt).HasColumnName("sent_at");

        // The panel pages one channel; the timeline pages the engagement.
        builder.HasIndex(m => new { m.ChannelId, m.SentAt }).HasDatabaseName("ix_chat_messages_channel_sent_at");
        builder.HasIndex(m => new { m.EngagementId, m.SentAt }).HasDatabaseName("ix_chat_messages_engagement_sent_at");
    }
}
