using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Presence;

/// <summary>
/// A snapshot of an implant's online presence in its engagement (roadmap M1.3).
/// Captures the moment it came online, the last time it was seen, and the
/// capability verbs it advertised at handshake. Immutable; presence updates
/// replace the record rather than mutating it.
/// </summary>
public sealed record PresenceRecord(
    ImplantId ImplantId,
    EngagementId EngagementId,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset OnlineAt,
    DateTimeOffset LastSeenAt);
