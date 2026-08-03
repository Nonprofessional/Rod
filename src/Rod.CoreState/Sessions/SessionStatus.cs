namespace Rod.CoreState.Sessions;

/// <summary>
/// Where a <see cref="Session"/> sits in its connection lifecycle
/// (architecture.md Sec 4.1, Sec 10.3).
///
/// - <see cref="Active"/>: the implant's beacon stream is open; the session is a
///   live execution context.
/// - <see cref="Closed"/>: the stream ended (clean close or abort); the session
///   stays in history but no longer holds the live connection.
/// </summary>
public enum SessionStatus
{
    /// <summary>The beacon stream is open and the implant is online.</summary>
    Active,

    /// <summary>The beacon stream has ended; the session is history.</summary>
    Closed,
}
