using Rod.CoreState.Live;

namespace Rod.CoreState.Webhooks;

/// <summary>
/// The forwarder's standing boundaries (architecture.md Sec 4.4): which
/// event kinds a subscription may name, what shape its URL may take, and
/// how many subscriptions one engagement may hold. These are domain rules,
/// not engine knobs -- every subscription passes them at creation, so a
/// subscription stored in the engagement is one the guards already
/// vouched for.
/// </summary>
public static class WebhookLimits
{
    /// <summary>
    /// How many subscriptions one engagement may hold. Each one is a push
    /// target riding every notifiable event; past a handful the problem is
    /// channel sprawl, not a limit to raise.
    /// </summary>
    public const int MaxSubscriptionsPerEngagement = 20;

    /// <summary>Bounds the subscription's display name.</summary>
    public const int MaxNameBytes = 200;

    /// <summary>Bounds the URL. Incoming-webhook URLs are short; this only stops absurdity.</summary>
    public const int MaxUrlBytes = 2048;

    /// <summary>
    /// How many consecutive delivery failures a subscription tolerates
    /// before the forwarder parks it -- three dead pushes in a row mean the
    /// receiver is gone, and a gone receiver should explain itself once in
    /// the trail, not on every event.
    /// </summary>
    public const int ConsecutiveFailureLimit = 3;

    /// <summary>
    /// The live event kinds a subscription may name. The engagement's
    /// operational beats are notifiable; operator presence is console
    /// chatter and channel output is a per-chunk firehose, so neither is.
    /// The shell kinds split from automation's trigger list: not triggerable
    /// there, but exactly what an off-console operator wants to hear about.
    /// </summary>
    public static bool IsNotifiableKind(LiveEventKind kind) => kind switch
    {
        LiveEventKind.SessionOpened => true,
        LiveEventKind.SessionClosed => true,
        LiveEventKind.TaskIssued => true,
        LiveEventKind.TaskCompleted => true,
        LiveEventKind.TaskCancelled => true,
        LiveEventKind.ImplantRetired => true,
        LiveEventKind.ShellSessionOpened => true,
        LiveEventKind.ShellSessionEnded => true,
        LiveEventKind.PayloadFetched => true,
        _ => false,
    };

    /// <summary>
    /// Checks a subscription URL. Null when acceptable; otherwise the
    /// readable refusal. The URL must be absolute https -- plain http is
    /// accepted only for loopback, the lab and self-hosted receivers -- and
    /// may carry no userinfo (credentials do not belong in a trail-adjacent
    /// string) or fragment (never sent). The query is deliberately free:
    /// it is where an incoming-webhook URL rides its bearer secret.
    /// </summary>
    public static string? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "A webhook subscription needs a URL.";
        if (url.Length > MaxUrlBytes)
            return $"The URL exceeds {MaxUrlBytes} bytes.";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return "The URL must be absolute.";
        if (uri.Scheme != Uri.UriSchemeHttps
            && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            return "The URL must be https; plain http is loopback-only (the lab and self-hosted receivers).";
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "The URL may not carry userinfo.";
        if (uri.Fragment.Length > 0)
            return "The URL may not carry a fragment.";
        return null;
    }
}
