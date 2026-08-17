using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Sessions;

/// <summary>
/// Tracks an implant's sessions -- one connected execution context per open
/// beacon stream (architecture.md Sec 4.1, Sec 10.3). A connecting implant that
/// completes its handshake opens a session; a disconnecting stream closes it.
/// Sessions are engagement-scoped so cross-engagement access stays impossible by
/// construction (architecture.md Sec 3).
///
/// Presence is the active-sessions projection: the implants currently online in
/// an engagement are exactly those with an Active session. There is no separate
/// live-state store -- the registry is the single source for both "is it online"
/// and the per-implant connection history.
///
/// The default is an in-memory implementation; the port keeps callers
/// agnostic to that.
/// </summary>
public interface ISessionRegistry
{
    /// <summary>
    /// Opens a new session for <paramref name="implant"/> in its engagement,
    /// recording the advertised <paramref name="capabilities"/>. The session
    /// starts Active and is the new live channel for the implant.
    /// </summary>
    Task<Session> OpenAsync(
        Implant implant,
        IReadOnlyCollection<string> capabilities,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Touches the active session for <paramref name="implant"/>: advances its
    /// last-seen time and refreshes the advertised capabilities. A no-op when the
    /// implant has no active session (e.g. a stray keepalive after close).
    /// </summary>
    Task TouchAsync(
        ImplantId implant,
        IReadOnlyCollection<string> capabilities,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the given session when it is active. A no-op if it was already
    /// closed (e.g. a duplicate close after a flap).
    /// </summary>
    Task CloseAsync(SessionId session, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>The session, or null when unknown.</summary>
    Task<Session?> FindAsync(SessionId session, CancellationToken cancellationToken = default);

    /// <summary>
    /// The implant's active session, or null when it is offline. An implant holds
    /// at most one active session at a time; a reconnect closes the prior one.
    /// </summary>
    Task<Session?> GetActiveAsync(ImplantId implant, CancellationToken cancellationToken = default);

    /// <summary>
    /// The implants currently online in an engagement -- their active sessions.
    /// This is the operator-visible "who is alive" view, scoped by engagement.
    /// </summary>
    Task<IReadOnlyList<Session>> ListActiveAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// All sessions an implant has held, oldest first -- its connection history.
    /// Active and closed sessions both appear; an implant that flapped shows one
    /// row per connect.
    /// </summary>
    Task<IReadOnlyList<Session>> ListByImplantAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes every Active session whose <see cref="Session.LastSeenAt"/> is
    /// older than <paramref name="cutoff"/> at <paramref name="at"/>, and returns
    /// the sessions it closed. This is the staleness sweep (architecture.md
    /// Sec 10.3): a beacon stream that dies silently -- the connection drops
    /// without a clean close, or the implant vanishes mid-stream -- leaves the
    /// session Active forever without it; sweeping it closed is what drops the
    /// implant off the online roster.
    /// </summary>
    Task<IReadOnlyList<Session>> SweepStaleAsync(
        DateTimeOffset cutoff,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);
}
