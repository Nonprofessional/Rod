namespace Rod.CoreState.ShellSessions;

/// <summary>
/// The lifecycle of a caught reverse shell's connection (architecture.md
/// Sec 8, the shellcatch transport). The session is Live from accept until
/// the socket dies; how it ended is the distinction the operator reads --
/// Lost means the peer went away on its own (network drop, target reboot,
/// the shell exiting), Closed means an operator ended it on purpose. Both
/// are terminal: a re-dialing shell is a new session, the same shape a
/// flapped implant session takes.
/// </summary>
public enum ShellSessionStatus
{
    /// <summary>The connection is up and the operator can interact with it.</summary>
    Live,

    /// <summary>
    /// The peer vanished without an operator closing it -- a read or write
    /// failure, or the inactivity timeout fired. Terminal.
    /// </summary>
    Lost,

    /// <summary>An operator closed the session deliberately. Terminal.</summary>
    Closed,
}
