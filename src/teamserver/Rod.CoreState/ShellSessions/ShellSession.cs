using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.ShellSessions;

/// <summary>
/// One caught reverse-shell connection -- a TCP accept that speaks no Rod
/// protocol (architecture.md Sec 8, the shellcatch transport). The peer is
/// whatever one-liner the operator ran on the target (nc, a bash /dev/tcp
/// pipe, a perl or python snippet); it never enrolls, never handshakes, and
/// carries no identity, so the session is scoped the only way an anonymous
/// arrival can be: by the engagement-scoped listener it landed on. That
/// listener binding is the isolation anchor -- a shell session is reachable
/// only through its engagement, the same construction every implant-scoped
/// entity follows (architecture.md Sec 3).
///
/// The entity carries the connection's lifecycle and the fingerprint the
/// server guessed from its output; the bytes themselves live in the
/// transport's buffers and the audit trail's transcript artifacts, not here.
/// An upgrade binds the implant the operator grew out of this shell
/// (architecture.md Sec 5.2's Ephemeral lineage), so the console can point
/// at the implant that replaced it.
/// </summary>
public sealed class ShellSession
{
    public ShellSessionId Id { get; }
    public EngagementId EngagementId { get; }

    /// <summary>
    /// The listener the shell landed on, by its raw id (the inner ring's
    /// convention for listener references -- <c>ListenerDefinition.Id</c>'s
    /// Guid, not the transport layer's typed id).
    /// </summary>
    public Guid ListenerId { get; }

    /// <summary>The remote endpoint as accepted, "host:port".</summary>
    public string RemoteAddress { get; }

    /// <summary>
    /// The server-side guess at host and shell program; Unknown until
    /// output gave the fingerprinter something to read.
    /// </summary>
    public ShellOsGuess Os { get; private set; }

    public ShellSessionStatus Status { get; private set; }
    public DateTimeOffset OpenedAt { get; }

    /// <summary>When operator input last flowed down the socket; null until the first write.</summary>
    public DateTimeOffset? LastInputAt { get; private set; }

    /// <summary>When shell output last flowed up; null until the first read.</summary>
    public DateTimeOffset? LastOutputAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    private ShellSession(
        ShellSessionId id,
        EngagementId engagementId,
        Guid listenerId,
        string remoteAddress,
        DateTimeOffset openedAt)
    {
        Id = id;
        EngagementId = engagementId;
        ListenerId = listenerId;
        RemoteAddress = remoteAddress;
        Os = ShellOsGuess.Unknown;
        Status = ShellSessionStatus.Live;
        OpenedAt = openedAt;
    }

    /// <summary>
    /// Factory for a freshly accepted connection. The session starts Live
    /// with an Unknown fingerprint -- the fingerprinter fills that in from
    /// the first output.
    /// </summary>
    public static ShellSession Open(
        ShellSessionId id,
        EngagementId engagementId,
        Guid listenerId,
        string remoteAddress,
        DateTimeOffset at)
        => new(id, engagementId, listenerId, remoteAddress, at);

    /// <summary>
    /// Records or refines the fingerprint. Only while Live and never away
    /// from Unknown toward a weaker guess: a later probe may sharpen an
    /// early read, but a concrete guess stands.
    /// </summary>
    public void MarkFingerprint(ShellOsGuess os)
    {
        if (Status != ShellSessionStatus.Live || os == ShellOsGuess.Unknown)
            return;
        Os = os;
    }

    /// <summary>Advances the last-input stamp. Only while Live.</summary>
    public void NoteInput(DateTimeOffset at)
    {
        if (Status != ShellSessionStatus.Live)
            return;
        LastInputAt = at;
    }

    /// <summary>Advances the last-output stamp. Only while Live.</summary>
    public void NoteOutput(DateTimeOffset at)
    {
        if (Status != ShellSessionStatus.Live)
            return;
        LastOutputAt = at;
    }

    /// <summary>
    /// The peer went away on its own. Returns whether this call ended the
    /// session; a duplicate mark after a close is a normal race (the read
    /// and write halves of one socket can fail together) and stays a no-op.
    /// </summary>
    public bool MarkLost(DateTimeOffset at)
    {
        if (Status != ShellSessionStatus.Live)
            return false;
        Status = ShellSessionStatus.Lost;
        EndedAt = at;
        return true;
    }

    /// <summary>
    /// An operator ended the session deliberately. Returns whether this
    /// call ended it, for the same duplicate-race reason as
    /// <see cref="MarkLost"/>.
    /// </summary>
    public bool Close(DateTimeOffset at)
    {
        if (Status != ShellSessionStatus.Live)
            return false;
        Status = ShellSessionStatus.Closed;
        EndedAt = at;
        return true;
    }
}
