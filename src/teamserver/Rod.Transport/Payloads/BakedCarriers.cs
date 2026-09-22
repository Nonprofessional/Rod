using Rod.Audit;
using Rod.CoreState.Transports;

namespace Rod.Transport.Payloads;

/// <summary>
/// Derives the carrier names an artifact's baked endpoints dial, off the
/// payload record the redeemed token resolves (the same record the enroll
/// path already reads for the contact key). The rule is the URL-shape
/// discipline the implant itself applies (architecture.md Sec 8): a schemed
/// http(s) endpoint runs the envelope POST cycle, and the beacon authority --
/// the one field the build parser guarantees is a bare host:port -- dials
/// the live beacon stream.
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

        // The beacon field's shape picks its carrier: the parser-guaranteed
        // bare authority dials the live stream, while the DNS family's
        // dns:// or doh:// dial names the TXT poll carrier -- the one
        // carrier with no channel support at all. Every other endpoint
        // must classify as a schemed web URL or the set is undeclared.
        if (!string.IsNullOrWhiteSpace(payload.BeaconEndpoint))
        {
            var beacon = payload.BeaconEndpoint.Trim();
            if (beacon.StartsWith("dns://", StringComparison.OrdinalIgnoreCase)
                || beacon.StartsWith("doh://", StringComparison.OrdinalIgnoreCase))
                Add(names, TransportCapabilities.DnsName);
            else
                Add(names, TransportCapabilities.BeaconStreamName);
        }
        if (!TryAddEndpoint(names, payload.Endpoint))
            return null;
        if (payload.Build?.FallbackEndpoints is { } fallbacks)
        {
            foreach (var fallback in fallbacks)
            {
                if (!TryAddEndpoint(names, fallback))
                    return null;
            }
        }

        // A stream-mode web build holds the WebSocket beacon open on its
        // front (the web posture's interactive tier), so the baked mode adds
        // the native carrier a web-front artifact actually dials. Poll-mode
        // and modeless records (an old build, a manual token) keep the
        // envelope-only answer, whose channel support is the
        // store-and-forward discipline every poll artifact advertises.
        if (string.Equals(payload.Build?.Mode, "stream", StringComparison.OrdinalIgnoreCase))
            Add(names, TransportCapabilities.BeaconStreamName);

        return names.Count == 0 ? null : names;
    }

    // Adds the carrier an endpoint's shape dials: a schemed http(s) endpoint
    // runs the envelope POST cycle, the DNS family's dns:// or doh:// dial
    // serves the TXT poll carrier, and the socket family's tcp:// dial
    // serves the one-connection-one-contact message-pipe carrier; an
    // empty field adds nothing, and an unrecognized shape returns false so
    // the caller undeclares the whole set instead of guessing.
    private static bool TryAddEndpoint(List<string> names, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return true;
        var trimmed = endpoint.Trim();
        if (trimmed.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            Add(names, TransportCapabilities.MessagePipeName);
            return true;
        }
        if (trimmed.StartsWith("dns://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("doh://", StringComparison.OrdinalIgnoreCase))
        {
            Add(names, TransportCapabilities.DnsName);
            return true;
        }
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
