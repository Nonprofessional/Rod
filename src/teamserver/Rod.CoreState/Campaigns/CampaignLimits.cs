using System.Text.RegularExpressions;

namespace Rod.CoreState.Campaigns;

/// <summary>
/// The campaign's standing boundaries (architecture.md Sec 11.5): scale
/// caps and the template's merge grammar. Domain rules, not engine knobs
/// -- every campaign passes them at creation, so a campaign stored in the
/// engagement is one the guards already vouched for. The refusal happens
/// at the seam (creation, not delivery), the same discipline webhook
/// subscriptions keep.
/// </summary>
public static partial class CampaignLimits
{
    /// <summary>
    /// How many recipients one campaign may carry. The per-recipient build
    /// is the price of enrollment attribution, which puts a campaign at
    /// spear-phish scale by construction -- a bulk send is not this
    /// surface, and the cap keeps a fat-fingered list from driving a
    /// build farm.
    /// </summary>
    public const int MaxRecipients = 200;

    /// <summary>Bounds the campaign's display name.</summary>
    public const int MaxNameBytes = 200;

    /// <summary>Bounds the subject; SMTP's own line-length ceiling makes
    /// anything longer a broken message anyway.</summary>
    public const int MaxSubjectBytes = 500;

    /// <summary>Bounds the body. Lure messages are short; this stops
    /// absurdity, not craft.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>Bounds the serialized build profile riding the row.</summary>
    public const int MaxBuildRequestBytes = 8192;

    /// <summary>Bounds a recipient's email or display name.</summary>
    public const int MaxAddressBytes = 320;

    /// <summary>Bounds the terminal failure reason on a recipient row.</summary>
    public const int MaxFailureBytes = 200;

    /// <summary>The merge fields a template may carry.</summary>
    public static readonly string[] MergeFields = ["link", "pixel", "email", "name"];

    // {{ field }} with optional slack inside the braces; case-insensitive so
    // a template pasted from a mail client's "smart" editor still merges.
    [GeneratedRegex(@"\{\{\s*([a-zA-Z]+)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderPattern();

    /// <summary>
    /// Checks a template pair. Null when acceptable; otherwise the
    /// readable refusal. The body must merge <c>{{link}}</c> -- a body
    /// without it delivers a message whose recipient has no way to reach
    /// the lure -- and every field named must be a known one. The subject
    /// may merge any known field and is not required to name any.
    /// </summary>
    public static string? ValidateTemplate(string? subject, string? body)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return "The campaign needs a subject.";
        if (subject.Length > MaxSubjectBytes)
            return $"The subject exceeds {MaxSubjectBytes} bytes.";
        if (string.IsNullOrWhiteSpace(body))
            return "The campaign needs a message body.";
        if (body.Length > MaxBodyBytes)
            return $"The body exceeds {MaxBodyBytes} bytes.";

        foreach (var (text, what) in new[] { (subject, "subject"), (body, "body") })
        {
            foreach (System.Text.RegularExpressions.Match match in PlaceholderPattern().Matches(text))
            {
                var field = match.Groups[1].Value.ToLowerInvariant();
                if (!MergeFields.Contains(field))
                    return $"Unknown merge field {{{{{match.Groups[1].Value}}}}} in the {what}; the known fields are {string.Join(", ", MergeFields.Select(f => $"{{{{{f}}}}}"))}.";
            }
        }

        if (!Merges(body, "link"))
            return "The body must merge {{link}} -- without it the recipient cannot reach the lure.";
        return null;
    }

    /// <summary>Whether a template text merges a given field.</summary>
    public static bool Merges(string template, string field)
        => PlaceholderPattern().Matches(template)
            .Any(m => string.Equals(m.Groups[1].Value, field, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Renders one template text for one recipient: every known field
    /// replaced, unknown ones having been refused at creation. A null name
    /// renders empty -- the field exists, the value does not.
    /// </summary>
    public static string Render(
        string template,
        string link,
        string pixel,
        string email,
        string? name)
        => PlaceholderPattern().Replace(template, m =>
               m.Groups[1].Value.ToLowerInvariant() switch
               {
                   "link" => link,
                   "pixel" => pixel,
                   "email" => email,
                   "name" => name ?? string.Empty,
                   _ => m.Value,
               });

    /// <summary>
    /// The pixel the engine auto-injects into an HTML body that did not
    /// place <c>{{pixel}}</c> itself: one 1x1 image tag appended before the
    /// closing body tag when there is one, at the end otherwise. Plain-text
    /// bodies cannot carry a pixel and are left alone -- opened stays
    /// best-effort by design.
    /// </summary>
    public static string AppendPixel(string htmlBody, string pixelUrl)
    {
        var tag = $"<img src=\"{pixelUrl}\" width=\"1\" height=\"1\" alt=\"\">";
        var closing = htmlBody.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return closing >= 0
            ? htmlBody.Insert(closing, tag)
            : htmlBody + tag;
    }
}
