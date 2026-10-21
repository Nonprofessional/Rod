using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.Operators.Collaboration;

namespace Rod.Operators.Endpoints;

/// <summary>
/// The engagement's chat surface (architecture.md Sec 15.3, ADR-005): the
/// coordination channels and their messages. Hosted by the operator layer;
/// scoped by the route engagement. Messages ride the timeline and SSE as
/// ordinary events and never enter the audit chain; they are append-only,
/// a correction being a new message.
/// </summary>
public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/engagements/{engagementId}/chat")
            .RequireAuthorization().AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Read));
        group.MapGet("/channels", ListChannelsAsync).WithName("ListChatChannels");
        group.MapPost("/channels", CreateChannelAsync).AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("CreateChatChannel");
        group.MapGet("/channels/{channelId}/messages", ListMessagesAsync).WithName("ListChatMessages");
        group.MapPost("/channels/{channelId}/messages", SendAsync).AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Write))
            .WithName("SendChatMessage");
        return endpoints;
    }

    private static async Task<IResult> ListChannelsAsync(
        string engagementId,
        ChatService chat,
        IEngagementRepository engagements,
        CancellationToken cancellationToken)
    {
        var (error, engagement) = await BindAsync(engagementId, engagements, cancellationToken);
        if (error is not null)
            return error;

        var channels = await chat.ListChannelsAsync(engagement, cancellationToken);
        return Results.Ok(new ChatChannelListResponse(
            channels.Select(ChatChannelResponse.Of).ToArray()));
    }

    private static async Task<IResult> CreateChannelAsync(
        string engagementId,
        CreateChannelRequest? body,
        ClaimsPrincipal user,
        ChatService chat,
        IEngagementRepository engagements,
        CancellationToken cancellationToken)
    {
        if (user.TryGetOperatorId() is not { } operatorId)
            return Results.Unauthorized();
        var (error, engagement) = await BindAsync(engagementId, engagements, cancellationToken);
        if (error is not null)
            return error;
        if (body is null || string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new Problem("A channel needs a name."));

        ChatChannel channel;
        try
        {
            channel = await chat.CreateChannelAsync(engagement, body.Name, operatorId, cancellationToken);
        }
        catch (ChatRejectedException ex)
        {
            return Results.Json(new Problem(ex.Message), statusCode: 422);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new Problem(ex.Message));
        }

        return Results.Created(
            $"/engagements/{engagement}/chat/channels/{channel.Id}",
            ChatChannelResponse.Of(channel));
    }

    private static async Task<IResult> ListMessagesAsync(
        string engagementId,
        string channelId,
        int? limit,
        string? cursor,
        ChatService chat,
        IEngagementRepository engagements,
        CancellationToken cancellationToken)
    {
        var (error, engagement) = await BindAsync(engagementId, engagements, cancellationToken);
        if (error is not null)
            return error;
        if (!ChatChannelId.TryParse(channelId, out var channel))
            return Results.BadRequest(new Problem("Channel id is not a valid identifier."));
        if (await chat.FindChannelAsync(engagement, channel, cancellationToken) is null)
            return Results.NotFound(new Problem("Channel does not exist in this engagement."));

        var boundLimit = limit ?? 50;
        if (boundLimit is < 1 or > 200)
            return Results.BadRequest(new Problem("limit must be between 1 and 200."));

        DateTimeOffset? before = null;
        Guid? beforeId = null;
        if (cursor is not null)
        {
            if (!TimestampIdCursor.TryDecode(cursor, out var afterTicks, out var afterId))
                return Results.BadRequest(new Problem("cursor is not a valid page cursor for this listing."));
            before = new DateTimeOffset(afterTicks, TimeSpan.Zero);
            beforeId = afterId;
        }

        var page = await chat.ListMessagesPageAsync(channel, boundLimit, before, beforeId, cancellationToken);
        var nextCursor = page.HasMore && page.Items.Count > 0
            ? TimestampIdCursor.Encode(page.Items[0].SentAt, page.Items[0].Id.Value)
            : null;
        return Results.Ok(new ChatMessageListResponse(
            page.Items.Select(ChatMessageResponse.Of).ToArray(), nextCursor));
    }

    private static async Task<IResult> SendAsync(
        string engagementId,
        string channelId,
        SendMessageRequest? body,
        ClaimsPrincipal user,
        ChatService chat,
        IEngagementRepository engagements,
        CancellationToken cancellationToken)
    {
        if (user.TryGetOperatorId() is not { } operatorId)
            return Results.Unauthorized();
        var (error, engagement) = await BindAsync(engagementId, engagements, cancellationToken);
        if (error is not null)
            return error;
        if (!ChatChannelId.TryParse(channelId, out var channel))
            return Results.BadRequest(new Problem("Channel id is not a valid identifier."));
        var found = await chat.FindChannelAsync(engagement, channel, cancellationToken);
        if (found is null)
            return Results.NotFound(new Problem("Channel does not exist in this engagement."));
        if (body is null || string.IsNullOrWhiteSpace(body.Body))
            return Results.BadRequest(new Problem("A chat message needs a body."));

        ChatMessage message;
        try
        {
            message = await chat.SendAsync(engagement, found, operatorId, body.Body, cancellationToken);
        }
        catch (ChatRejectedException ex)
        {
            return Results.Json(new Problem(ex.Message), statusCode: 422);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new Problem(ex.Message));
        }

        return Results.Created(
            $"/engagements/{engagement}/chat/channels/{channel}/messages/{message.Id}",
            ChatMessageResponse.Of(message));
    }

    private static async Task<(IResult? Error, EngagementId Engagement)> BindAsync(
        string engagementId,
        IEngagementRepository engagements,
        CancellationToken cancellationToken)
    {
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return (Results.BadRequest(new Problem("Engagement id is not a valid identifier.")), engagement);
        if (await engagements.FindAsync(engagement, cancellationToken) is null)
            return (Results.NotFound(new Problem("Unknown engagement.")), engagement);
        return (null, engagement);
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record CreateChannelRequest(string? Name);

    public sealed record SendMessageRequest(string? Body);

    public sealed record ChatChannelResponse(
        string Id,
        string Name,
        string CreatedBy,
        DateTimeOffset CreatedAt)
    {
        public static ChatChannelResponse Of(ChatChannel c)
            => new(c.Id.ToString(), c.Name, c.CreatedBy.ToString(), c.CreatedAt);
    }

    public sealed record ChatMessageResponse(
        string Id,
        string ChannelId,
        string AuthorId,
        string Body,
        DateTimeOffset SentAt)
    {
        public static ChatMessageResponse Of(ChatMessage m)
            => new(m.Id.ToString(), m.ChannelId.ToString(), m.AuthorId.ToString(), m.Body, m.SentAt);
    }

    public sealed record ChatChannelListResponse(IReadOnlyList<ChatChannelResponse> Items);

    public sealed record ChatMessageListResponse(IReadOnlyList<ChatMessageResponse> Items, string? NextCursor);
}
