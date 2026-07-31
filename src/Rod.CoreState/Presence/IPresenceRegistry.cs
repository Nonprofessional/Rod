using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Presence;

/// <summary>
/// Tracks which implants are currently online in their engagements (roadmap
/// M1.3). The walking skeleton ships an in-memory implementation; the port keeps
/// callers agnostic to that. A connecting implant that completes its handshake
/// is marked online; a disconnecting stream is marked offline. This is the
/// operator-visible "is this implant alive" view, scoped by engagement so
/// cross-engagement access stays impossible by construction (architecture.md
/// Sec 3).
///
/// Presence is intentionally minimal here: it records online/offline, the last
/// seen time, and the advertised capabilities. A first-class <c>Session</c>
/// aggregate arrives with the core-state layer (roadmap M2.1).
/// </summary>
public interface IPresenceRegistry
{
    /// <summary>
    /// Marks <paramref name="implant"/> online in its engagement, recording the
    /// advertised capabilities. Overwrites any prior record for that implant.
    /// </summary>
    Task SetOnlineAsync(
        Implant implant,
        IReadOnlyCollection<string> capabilities,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>Mark the implant offline. A no-op if it was not online.</summary>
    Task SetOfflineAsync(ImplantId implant, CancellationToken cancellationToken = default);

    /// <summary>The implant's current presence, or null when it is offline/unknown.</summary>
    Task<PresenceRecord?> FindAsync(ImplantId implant, CancellationToken cancellationToken = default);

    /// <summary>The implants currently online in an engagement.</summary>
    Task<IReadOnlyList<PresenceRecord>> ListOnlineAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default);
}
