using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.V1;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The handshake pieces every contact transport shares: the refusal map, the
/// response builder, and the flood-guarded SessionOpened audit write. Each
/// transport authenticates differently (a client certificate over mTLS, the
/// sealed envelope's artifact key over the web posture, the handshake id over
/// the certificate-less family), so identity resolution stays with the
/// transport -- everything downstream of the HandshakeService result is this
/// class, one definition instead of one per endpoint.
/// </summary>
internal static class BeaconHandshake
{
    /// <summary>
    /// Maps a <see cref="HandshakeException"/> reason onto the wire status the
    /// implant reads, the same table for every transport.
    /// </summary>
    internal static HandshakeStatus MapStatus(HandshakeReason reason) => reason switch
    {
        HandshakeReason.UnknownImplant => HandshakeStatus.UnknownImplant,
        HandshakeReason.VersionMismatch => HandshakeStatus.VersionMismatch,
        HandshakeReason.IdentityMismatch => HandshakeStatus.IdentityMismatch,
        HandshakeReason.KillDateExpired => HandshakeStatus.KillDateExpired,
        HandshakeReason.ImplantRetired => HandshakeStatus.ImplantRetired,
        _ => HandshakeStatus.Unspecified,
    };

    /// <summary>
    /// Builds the handshake response: the server's protocol version and the
    /// negotiated arms (replay nonces, receive acks) ride the echo so the
    /// implant knows its verification and delivery posture.
    /// </summary>
    internal static HandshakeResponse Response(
        HandshakeStatus status, string? engagementId, bool replayNonces, bool taskAcks = false)
        => new()
        {
            Status = status,
            Version = new ProtocolVersion { Major = ProtocolVersions.Major, Minor = ProtocolVersions.Minor },
            EngagementId = engagementId ?? string.Empty,
            ReplayNonces = replayNonces,
            TaskAcks = taskAcks,
        };

    /// <summary>
    /// Records a genuinely new session (architecture.md Sec 11). A reused one
    /// (a reconnect -- a poll contact or a flapped stream) is not: the session
    /// entity and its SessionOpened record already exist, and a poll cadence
    /// must not flood the engagement trail. A handshake is implant-initiated,
    /// so the event is attributed to the operator who deployed the implant;
    /// the payload carries the negotiated protocol version and the session id
    /// as the outcome.
    /// </summary>
    internal static Task AppendSessionOpenedAsync(
        IAuditStore audit, HandshakeResult handshake, HandshakeRequest request)
    {
        if (handshake.ReusedSession)
            return Task.CompletedTask;
        return audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: handshake.EngagementId.Value,
                operatorId: handshake.DeployedBy.Value,
                implantId: handshake.ImplantId.Value,
                taskId: Guid.Empty,
                verb: "handshake",
                kind: AuditEventKind.SessionOpened,
                payload: $"{request.Version?.Major ?? 0}.{request.Version?.Minor ?? 0}",
                output: null,
                outcome: handshake.SessionId.ToString(),
                at: handshake.At),
            CancellationToken.None);
    }
}
