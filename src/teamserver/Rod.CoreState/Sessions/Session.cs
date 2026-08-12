using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Sessions;

/// <summary>
/// A session -- one connected execution context for an implant in its
/// engagement (architecture.md Sec 4.1, Sec 10.3). An implant opens a session on
/// a successful handshake and closes it when the beacon stream ends. An implant
/// may hold many sessions over its life (reconnects, flaps), so the session is
/// the per-connection entity while the implant is the per-host entity. Tasks are
/// dispatched against the implant; the session is the live channel they flow
/// over.
///
/// Carries the capabilities advertised at handshake (architecture.md Sec 10) and
/// a <see cref="LastSeenAt"/> advanced by each handshake/keepalive, which is what
/// "is this implant alive" reads as. Entity shape only: this type holds the
/// lifecycle and the advertised capabilities; it stays free of the transport.
/// </summary>
public sealed class Session
{
    public SessionId Id { get; }
    public ImplantId ImplantId { get; }
    public EngagementId EngagementId { get; }
    public IReadOnlyList<string> Capabilities { get; private set; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public SessionStatus Status { get; private set; }

    private Session(
        SessionId id,
        ImplantId implantId,
        EngagementId engagementId,
        IReadOnlyList<string> capabilities,
        DateTimeOffset startedAt)
    {
        Id = id;
        ImplantId = implantId;
        EngagementId = engagementId;
        Capabilities = capabilities;
        StartedAt = startedAt;
        LastSeenAt = startedAt;
        Status = SessionStatus.Active;
    }

    /// <summary>
    /// Factory for a freshly opened session. <paramref name="capabilities"/> are
    /// the verbs the implant advertised at handshake; they gate tasking dispatch
    /// (architecture.md Sec 10). The session starts <see cref="SessionStatus.Active"/>.
    /// </summary>
    public static Session Open(
        SessionId id,
        ImplantId implantId,
        EngagementId engagementId,
        IReadOnlyCollection<string> capabilities,
        DateTimeOffset at)
    {
        var caps = capabilities is null || capabilities.Count == 0
            ? Array.Empty<string>()
            : capabilities.ToArray();
        return new Session(id, implantId, engagementId, caps, at);
    }

    /// <summary>
    /// Advances <see cref="LastSeenAt"/> and refreshes the advertised
    /// capabilities. Called on each handshake/keepalive. Only legal while Active;
    /// a closed session no longer holds a live connection to refresh.
    /// </summary>
    public void Touch(IReadOnlyCollection<string> capabilities, DateTimeOffset at)
    {
        if (Status != SessionStatus.Active)
            throw new InvalidOperationException($"Session {Id} cannot be touched from {Status}.");

        Capabilities = capabilities is null || capabilities.Count == 0
            ? Array.Empty<string>()
            : capabilities.ToArray();
        LastSeenAt = at;
    }

    /// <summary>
    /// Marks the session ended. Only legal from Active. After this the session
    /// stays in history (it is the per-connection record) but no longer reads as
    /// online.
    /// </summary>
    public void Close(DateTimeOffset at)
    {
        if (Status != SessionStatus.Active)
            throw new InvalidOperationException($"Session {Id} cannot be closed from {Status}.");

        EndedAt = at;
        Status = SessionStatus.Closed;
    }
}
