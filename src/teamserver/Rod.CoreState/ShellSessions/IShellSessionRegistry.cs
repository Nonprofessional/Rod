using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.ShellSessions;

/// <summary>
/// Tracks caught reverse-shell sessions -- one per accepted no-protocol
/// connection on a shellcatch listener (architecture.md Sec 8). This is the
/// shell-facing sibling of <see cref="Sessions.ISessionRegistry"/>: an
/// implant session follows a handshake that carried an identity, a shell
/// session follows nothing but the accept, so the two registries stay
/// separate rather than one carrying a null-identity degenerate case.
///
/// Sessions are engagement-scoped (the listener they landed on is
/// engagement-bound, and that binding is the isolation anchor), so
/// cross-engagement access stays impossible by construction
/// (architecture.md Sec 3).
///
/// The default is an in-memory implementation; the port keeps callers
/// agnostic to that.
/// </summary>
public interface IShellSessionRegistry
{
    /// <summary>
    /// Opens a session for a connection accepted on <paramref name="listener"/>
    /// from <paramref name="remoteAddress"/>. The session starts Live with an
    /// Unknown fingerprint.
    /// </summary>
    Task<ShellSession> OpenAsync(
        EngagementId engagement,
        Guid listenerId,
        string remoteAddress,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>Records or refines the session's fingerprint. A no-op when the session is unknown.</summary>
    Task MarkFingerprintAsync(
        ShellSessionId session,
        ShellOsGuess os,
        CancellationToken cancellationToken = default);

    /// <summary>Advances the session's last-input stamp. A no-op when the session is unknown.</summary>
    Task NoteInputAsync(
        ShellSessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>Advances the session's last-output stamp. A no-op when the session is unknown.</summary>
    Task NoteOutputAsync(
        ShellSessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the session Lost -- the peer went away. A no-op when the
    /// session is unknown or already ended.
    /// </summary>
    Task MarkLostAsync(
        ShellSessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the session Closed by an operator. A no-op when the session is
    /// unknown or already ended.
    /// </summary>
    Task CloseAsync(
        ShellSessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Binds the implant this shell grew into (the upgrade path's
    /// enrollment callback). First binding wins.
    /// </summary>
    Task BindUpgradeAsync(
        ShellSessionId session,
        ImplantId implant,
        CancellationToken cancellationToken = default);

    /// <summary>The session, or null when unknown.</summary>
    Task<ShellSession?> FindAsync(ShellSessionId session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every shell session in the engagement, oldest first -- live, lost,
    /// and closed alike; ended sessions stay readable as the engagement's
    /// connection history.
    /// </summary>
    Task<IReadOnlyList<ShellSession>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default);
}
