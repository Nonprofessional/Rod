using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Sessions;

namespace Rod.CoreState.Application;

/// <summary>
/// The protocol versions the server speaks (architecture.md Sec 8). The wire
/// <c>ProtocolVersion</c> carries major/minor; a matching major version with a
/// server-supported minor is accepted. Kept here so the core, not the transport,
/// owns the negotiation floor.
/// </summary>
public static class ProtocolVersions
{
    /// <summary>The current (and only) protocol generation Rod speaks.</summary>
    public const int Major = 1;

    /// <summary>The highest minor version of <see cref="Major"/> the server supports.</summary>
    public const int Minor = 0;

    /// <summary>
    /// True when <paramref name="major"/>/<paramref name="minor"/> is a version
    /// this server accepts. A newer major is incompatible; the server tolerates
    /// clients at or below its minor for backward compatibility.
    /// </summary>
    public static bool IsSupported(int major, int minor)
        => major == Major && minor <= Minor;
}

/// <summary>
/// The handshake use case: an implant that just opened a beacon
/// stream advertises its protocol version, identity, and capabilities. The
/// service confirms the implant is enrolled, verifies the certificate binding
/// matches the enrolled engagement (the mTLS identity check, architecture.md
/// Sec 9), checks the protocol version, refuses an implant past its baked-in
/// kill date (architecture.md Sec 7), and opens a session for the implant in
/// its engagement. Refusals throw <see cref="HandshakeException"/> with a reason
/// the transport maps to a wire status; like <see cref="EnrollmentService"/> it
/// holds no state of its own.
/// </summary>
public sealed class HandshakeService
{
    private readonly IImplantRepository _implants;
    private readonly ISessionRegistry _sessions;
    private readonly TimeProvider _clock;

    public HandshakeService(
        IImplantRepository implants,
        ISessionRegistry sessions,
        TimeProvider clock)
    {
        _implants = implants;
        _sessions = sessions;
        _clock = clock;
    }

    /// <summary>
    /// Performs the handshake. <paramref name="command"/> carries the wire
    /// advertisement and the engagement id bound into the presenting client
    /// certificate (read by the transport from the Rod engagement extension,
    /// architecture.md Sec 9). When that binding disagrees with the implant's
    /// enrolled engagement, the connection is an identity mismatch.
    /// </summary>
    public async Task<HandshakeResult> HandshakeAsync(
        HandshakeCommand command,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // 1. Protocol version. Checked first: it is cheap and shapes nothing else.
        if (!ProtocolVersions.IsSupported(command.MajorVersion, command.MinorVersion))
        {
            throw new HandshakeException(
                HandshakeReason.VersionMismatch,
                $"Unsupported protocol version {command.MajorVersion}.{command.MinorVersion}; " +
                $"this server speaks {ProtocolVersions.Major}.{ProtocolVersions.Minor}.");
        }

        // 2. Implant must be enrolled. An unknown id never gets a session.
        var implant = await _implants.FindAsync(command.ImplantId, cancellationToken);
        if (implant is null)
        {
            throw new HandshakeException(
                HandshakeReason.UnknownImplant,
                $"Implant {command.ImplantId} is not enrolled.");
        }

        // 3. mTLS identity check (architecture.md Sec 9): when the transport
        //    carried a client certificate, the engagement bound into it must
        //    equal the implant's enrolled engagement -- the certificate already
        //    chained to the CA at the transport layer, and this is the
        //    application-layer binding that complements it. A null binding is
        //    the certificate-less transport posture (Sec 8): the named-pipe
        //    and raw-TCP listeners carry no mTLS, so the implant is identified
        //    by its id alone -- the same tradeoff DNS documents, extended to a
        //    handshake-capable transport. The enrolled, kill-date, and retired
        //    gates still apply in full.
        if (command.CertificateEngagementId is { } bound && bound != implant.EngagementId)
        {
            throw new HandshakeException(
                HandshakeReason.IdentityMismatch,
                $"Client certificate engagement does not match implant {implant.Id}'s engagement.");
        }

        // 4. Kill date (architecture.md Sec 7). A lost implant self-terminates at
        //    its baked-in kill date; the teamserver mirrors that here by refusing
        //    to open a session for an implant whose kill date has passed. The
        //    implant entity carries the kill date set at enrollment; the wall
        //    clock here is authoritative.
        if (now > implant.KillDate)
        {
            throw new HandshakeException(
                HandshakeReason.KillDateExpired,
                $"Implant {implant.Id} kill date {implant.KillDate:O} has passed.");
        }

        // 5. Retirement (architecture.md Sec 7). An implant taken out of
        //    operation never gets a session again. Retirement is an explicit
        //    operator action (distinct from the time-based kill date above), so it
        //    sits between the kill-date check and session open: a retired implant
        //    is refused even when it is otherwise still within its kill window.
        if (implant.IsRetired)
        {
            throw new HandshakeException(
                HandshakeReason.ImplantRetired,
                $"Implant {implant.Id} was retired at {implant.RetiredAt:O}.");
        }

        // 6. Open (or reuse) the session. The capabilities advertised here gate
        // tasking dispatch (architecture.md Sec 10). A session is the
        // implant's live channel, not one TCP connection: the registry reuses
        // the active session on a reconnect (a poll check-in or a flapped
        // stream) and only opens a new entity after the prior one closed.
        // Whether this handshake reused one is returned so the transport
        // writes the SessionOpened audit record only for a genuinely new
        // session -- a poll cadence must not flood the engagement trail.
        var priorActive = await _sessions.GetActiveAsync(command.ImplantId, cancellationToken);
        var session = await _sessions.OpenAsync(implant, command.Capabilities, now, cancellationToken);

        // 7. Replay-nonce negotiation (architecture.md Sec 9 -- tasking replay
        //    nonces). An implant that advertises the arm gets it for life: the
        //    flag is sticky on the implant, so every later dispatch -- any
        //    session, any transport -- carries the nonce shape, and a handshake
        //    that stops advertising cannot downgrade tasking back to the
        //    nonce-less form. The result carries the effective state so the
        //    transport echoes it on the wire and the implant knows which
        //    verification posture to enforce.
        var replayNonces = implant.ReplayNonces || command.ReplayNonces;
        if (command.ReplayNonces && !implant.ReplayNonces)
        {
            implant.EnableReplayNonces();
            await _implants.SaveAsync(implant, cancellationToken);
        }

        return new HandshakeResult(
            session.Id, implant.Id, implant.EngagementId, implant.DeployedBy, now,
            ReusedSession: priorActive is not null,
            ReplayNonces: replayNonces);
    }
}

/// <summary>
/// Request to complete a handshake. <see cref="MajorVersion"/>/
/// <see cref="MinorVersion"/> are the wire <c>ProtocolVersion</c>;
/// <see cref="ImplantId"/> is the implant's enrolled id;
/// <see cref="Capabilities"/> are the verbs it advertises;
/// <see cref="CertificateEngagementId"/> is the engagement id bound into the
/// presenting client certificate (read by the transport from the Rod engagement
/// extension, architecture.md Sec 9), or null when the transport carries no
/// client certificate -- the certificate-less posture the stream listeners use
/// (Sec 8), where the implant is identified by its id alone and the binding
/// check does not apply. The service compares a present binding against the
/// implant's enrolled engagement for the mTLS identity check.
/// <see cref="ReplayNonces"/> is the implant's advertisement of the tasking
/// replay-nonce arm (architecture.md Sec 9); the service makes it sticky on the
/// implant and reports the effective state back on the result.
/// </summary>
public sealed record HandshakeCommand(
    ImplantId ImplantId,
    int MajorVersion,
    int MinorVersion,
    IReadOnlyCollection<string> Capabilities,
    EngagementId? CertificateEngagementId,
    bool ReplayNonces = false);

/// <summary>
/// Result of a successful handshake: the session the implant holds (freshly
/// opened, or the active one reused on a reconnect), its identity, its
/// (confirmed) engagement, the operator who deployed it (used to attribute the
/// session-opening event, since a handshake is implant-initiated), and the time
/// of the handshake. <see cref="ReusedSession"/> tells the transport this
/// handshake refreshed an existing session, so the SessionOpened audit record is
/// written only for a new one. The transport echoes the engagement id back so
/// the implant can confirm its binding. <see cref="ReplayNonces"/> is the
/// effective replay-nonce state for this implant (sticky once advertised), for
/// the transport to echo on the handshake response.
/// </summary>
public sealed record HandshakeResult(
    SessionId SessionId,
    ImplantId ImplantId,
    EngagementId EngagementId,
    OperatorId DeployedBy,
    DateTimeOffset At,
    bool ReusedSession = false,
    bool ReplayNonces = false);
