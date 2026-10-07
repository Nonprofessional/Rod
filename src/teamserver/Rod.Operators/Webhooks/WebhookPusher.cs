using System.Text;
using System.Text.Json;
using Rod.CoreState;
using Rod.CoreState.Live;
using Rod.CoreState.Webhooks;

namespace Rod.Operators.Webhooks;

/// <summary>
/// One push to a registered channel: the POST the forwarder makes per
/// matching event and the <c>:test</c> action rides. The body is the SSE
/// frame shape verbatim (kind, engagement, operator, implant, task ids,
/// payload, timestamp), so a channel receives exactly what a connected
/// console receives. Single-attempt, bounded by the client's timeout --
/// retry is deliberately absent (architecture.md Sec 4.4): the trail
/// records the miss, the engagement does not carry a backlog.
///
/// The client is taken lazily from the factory on the first push. Creating
/// it at construction time would activate the factory's handler cache --
/// and its cleanup cycle -- in every host that merely registers the
/// forwarder, pinning each short-lived host's configuration root (and its
/// file watcher) long past its disposal; a test process that builds
/// hundreds of hosts exhausts the system's inotify instances that way.
/// </summary>
public sealed class WebhookPusher
{
    /// <summary>
    /// The kind string a <c>:test</c> frame carries. A plain string, not a
    /// <see cref="LiveEventKind"/>: the test frame never rode the bus, and
    /// a receiver should tell it from every real event by inspection.
    /// </summary>
    public const string TestKind = "test";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _factory;
    private HttpClient? _client;

    public WebhookPusher(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client => _client ??= _factory.CreateClient("webhooks");

    /// <summary>
    /// The outcome of one push: delivered on any 2xx, otherwise the
    /// readable refusal. Never throws -- delivery problems are data for
    /// the trail, not faults in the caller.
    /// </summary>
    public sealed record Result(bool Delivered, string Outcome, string Reason)
    {
        public static Result FromStatus(int status)
            => status is >= 200 and < 300
                ? new Result(true, $"delivered:{status}", $"HTTP {status}")
                : new Result(false, $"failed:{status}", $"HTTP {status}");

        public static Result FromTransport(string reason)
            => new(false, $"failed:{reason}", reason);
    }

    public async Task<Result> PushAsync(
        string url,
        string jsonBody,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var response = await Client.PostAsync(url, content, cancellationToken);
            return Result.FromStatus((int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per-request budget ran out, not the pump's lifetime.
            return Result.FromTransport("timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return Result.FromTransport(ex is HttpRequestException re && re.Message.Length > 0
                ? FirstLine(re.Message)
                : ex.GetType().Name.ToLowerInvariant());
        }
    }

    /// <summary>The frame a matching live event pushes.</summary>
    public static string Frame(LiveEvent @event)
        => JsonSerializer.Serialize(
            new
            {
                kind = @event.Kind.ToString(),
                engagementId = @event.EngagementId.ToString(),
                operatorId = @event.OperatorId.ToString(),
                implantId = @event.ImplantId?.ToString(),
                taskId = @event.TaskId?.ToString(),
                payload = @event.Payload,
                at = @event.At,
            },
            Json);

    /// <summary>The frame a <c>:test</c> pushes: the same shape, a kind no real event carries.</summary>
    public static string TestFrame(EngagementId engagement, string subscriptionName, DateTimeOffset at)
        => JsonSerializer.Serialize(
            new
            {
                kind = TestKind,
                engagementId = engagement.ToString(),
                operatorId = string.Empty,
                implantId = (string?)null,
                taskId = (string?)null,
                payload = $"webhook test for '{subscriptionName}'",
                at,
            },
            Json);

    private static string FirstLine(string text)
    {
        var separator = text.IndexOf('\n');
        return separator is > 0 and var stop ? text[..stop].Trim() : text.Trim();
    }
}
