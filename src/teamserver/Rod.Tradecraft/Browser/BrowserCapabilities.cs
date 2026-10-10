using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Browser;

/// <summary>
/// The browser hook verbs (architecture.md Sec 10.1, the "browser" category;
/// Sec 5.2, the Browser class's reduced set): the read-and-steer actions a
/// hooked page justifies. The handlers run inside the served hook script
/// over the envelope carrier, and their results land in the audit trail like
/// any other implant's.
/// </summary>
/// <remarks>
/// The set is deliberately mainstream: everything past it -- input capture in
/// a hooked page (keylogging, form-field harvesting) and browser-exploit
/// chaining -- is an out-of-tree capability contract, never a core verb
/// (architecture.md Sec 13), the same line the evasion and exploit
/// categories draw. <see cref="Prompt"/> answers with what the user typed,
/// so it carries <c>reads-input</c> and the sensitive-verb policy holds it
/// out of automation (Sec 10.4); the rest are read or steer actions with no
/// attribute beyond their documented effect.
/// </remarks>
public static class BrowserCapabilities
{
    /// <summary>Report the hooked browser's navigator facts as JSON.</summary>
    public const string Fingerprint = "browser.fingerprint";

    /// <summary>
    /// Read the current origin's <c>document.cookie</c> (non-HttpOnly only --
    /// the browser's own boundary, documented rather than worked around).
    /// </summary>
    public const string Cookies = "browser.cookies";

    /// <summary>
    /// Return the page's <c>outerHTML</c>, optionally narrowed by a CSS
    /// selector argument, truncated at 1 MiB.
    /// </summary>
    public const string Dom = "browser.dom";

    /// <summary>
    /// Rasterize the DOM (the SVG foreignObject shape, cross-origin imagery
    /// stripped) into a PNG artifact through the exfil-chunk path.
    /// </summary>
    public const string Screenshot = "browser.screenshot";

    /// <summary>
    /// Navigate the tab to the argument URL -- the hook dies with the page
    /// it left.
    /// </summary>
    public const string Redirect = "browser.redirect";

    /// <summary>
    /// Show the user a <c>window.prompt</c> with the argument text and
    /// return the answer.
    /// </summary>
    public const string Prompt = "browser.prompt";

    private static readonly IReadOnlyDictionary<string, string> ReadsCookies =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["reads-cookies"] = "true",
        };

    private static readonly IReadOnlyDictionary<string, string> ReadsPage =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["reads-page"] = "true",
        };

    private static readonly IReadOnlyDictionary<string, string> CapturesScreen =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["captures-screen"] = "true",
        };

    private static readonly IReadOnlyDictionary<string, string> NavigatesAway =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["navigates-away"] = "true",
        };

    private static readonly IReadOnlyDictionary<string, string> ReadsInput =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["reads-input"] = "true",
        };

    /// <summary>
    /// Descriptors for every browser verb, in declared order. The composition
    /// root registers these so the registry lists the full hook set.
    /// </summary>
    public static readonly CapabilityDescriptor[] All =
    {
        CapabilityDescriptor.Of(Fingerprint, CapabilityCategory.Browser, "1.0"),
        CapabilityDescriptor.Of(Cookies, CapabilityCategory.Browser, "1.0", ReadsCookies),
        CapabilityDescriptor.Of(Dom, CapabilityCategory.Browser, "1.0", ReadsPage),
        CapabilityDescriptor.Of(Screenshot, CapabilityCategory.Browser, "1.0", CapturesScreen),
        CapabilityDescriptor.Of(Redirect, CapabilityCategory.Browser, "1.0", NavigatesAway),
        CapabilityDescriptor.Of(Prompt, CapabilityCategory.Browser, "1.0", ReadsInput),
    };

    /// <summary>Every browser verb string, in declared order.</summary>
    public static readonly string[] Verbs =
    {
        Fingerprint, Cookies, Dom, Screenshot, Redirect, Prompt,
    };
}
