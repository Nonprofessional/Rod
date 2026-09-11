using Rod.Audit;
using Rod.CoreState.Transports;

namespace Rod.Transport.Payloads;

/// <summary>
/// Derives the carrier names an artifact's baked endpoints dial, off the
/// payload record the redeemed token resolves (the same record the enroll
/// path already reads for the check-in key). The rule is the URL-shape
/// discipline the implant itself applies (architecture.md Sec 8): a schemed
/// http(s) endpoint runs the envelope POST cycle, and the beacon authority --
/// the one field the build parser guarantees is the bare mTLS socket -- dials
/// the gRPC stream.
/// </summary>
/// <remarks>
/// The derivation is deliberately conservative in one direction: an endpoint
/// whose shape the rule does not recognize (a DNS zone, a pipe path, a
/// carrier a later transport introduced) leaves the whole set undeclared
/// rather than guessing. A false "envelope-only" would refuse channel tasking
/// at issuance for an artifact that could claim it; a false "native" only
/// keeps the older behavior, the dispatch-time deferral. Unknown therefore
/// means permissive, never refusal.
/// </remarks>
public static class BakedCarriers
{
    /// <summary>
    /// The carrier names for <paramref name="payload"/>'s baked endpoints
    /// (beacon, enroll front, walk fallbacks), deduplicated; or null when no
    /// record resolved, no endpoint was recorded, or any non-beacon endpoint's
    /// shape is unrecognized.
    /// </summary>
    public static IReadOnlyList<string>? From(PayloadRecord? payload)
    {
        if (payload is null)
            return null;

        var names = new List<string>();

        // The beacon field is the parser-guaranteed mTLS authority, so it
        // dials the stream whatever string it holds; every other endpoint
        // must classify as a schemed web URL or the set is undeclared.
        if (!string.IsNullOrWhiteSpace(payload.BeaconEndpoint))
            Add(names, TransportCapabilities.BeaconStreamName);
        if (!TryAddWeb(names, payload.Endpoint))
            return null;
        if (payload.Build?.FallbackEndpoints is { } fallbacks)
        {
            foreach (var fallback in fallbacks)
            {
                if (!TryAddWeb(names, fallback))
                    return null;
            }
        }

        return names.Count == 0 ? null : names;
    }

    // Adds the envelope carrier for a schemed endpoint; an empty field adds
    // nothing, and an unrecognized shape returns false so the caller
    // undeclares the whole set instead of guessing.
    private static bool TryAddWeb(List<string> names, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return true;
        var trimmed = endpoint.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        Add(names, TransportCapabilities.EnvelopeName);
        return true;
    }

    private static void Add(List<string> names, string name)
    {
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            names.Add(name);
    }
}
