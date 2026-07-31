using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Presence;

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
/// The handshake use case (roadmap M1.3): an implant that just opened a beacon
/// stream advertises its protocol version, identity, and capabilities. The
/// service confirms the implant is enrolled, verifies the certificate binding
/// matches the enrolled engagement (the mTLS identity check, architecture.md
/// Sec 9), checks the protocol version, and records presence in the implant's
/// engagement. Refusals throw <see cref="HandshakeException"/> with a reason the
/// transport maps to a wire status; like <see cref="EnrollmentService"/> it
/// holds no state of its own.
/// </summary>
public sealed class HandshakeService
{
    private readonly IImplantRepository _implants;
    private readonly IPresenceRegistry _presence;
    private readonly TimeProvider _clock;

    public HandshakeService(
        IImplantRepository implants,
        IPresenceRegistry presence,
        TimeProvider clock)
    {
        _implants = implants;
        _presence = presence;
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

        // 2. Implant must be enrolled. An unknown id never gets presence.
        var implant = await _implants.FindAsync(command.ImplantId, cancellationToken);
        if (implant is null)
        {
            throw new HandshakeException(
                HandshakeReason.UnknownImplant,
                $"Implant {command.ImplantId} is not enrolled.");
        }

        // 3. mTLS identity check (architecture.md Sec 9): the engagement bound
        //    into the certificate must equal the implant's enrolled engagement.
        //    The certificate already chained to the CA at the transport layer;
        //    this is the application-layer binding that complements it.
        if (command.CertificateEngagementId is null
            || command.CertificateEngagementId != implant.EngagementId)
        {
            throw new HandshakeException(
                HandshakeReason.IdentityMismatch,
                $"Client certificate engagement does not match implant {implant.Id}'s engagement.");
        }

        // 4. Record presence. The capabilities advertised here gate tasking
        //    dispatch in later milestones (architecture.md Sec 10).
        await _presence.SetOnlineAsync(implant, command.Capabilities, now, cancellationToken);

        return new HandshakeResult(implant.Id, implant.EngagementId, now);
    }
}

/// <summary>
/// Request to complete a handshake. <see cref="MajorVersion"/>/
/// <see cref="MinorVersion"/> are the wire <c>ProtocolVersion</c>;
/// <see cref="ImplantId"/> is the implant's enrolled id;
/// <see cref="Capabilities"/> are the verbs it advertises;
/// <see cref="CertificateEngagementId"/> is the engagement id bound into the
/// presenting client certificate (read by the transport from the Rod engagement
/// extension, architecture.md Sec 9). The service compares it against the
/// implant's enrolled engagement for the mTLS identity check.
/// </summary>
public sealed record HandshakeCommand(
    ImplantId ImplantId,
    int MajorVersion,
    int MinorVersion,
    IReadOnlyCollection<string> Capabilities,
    EngagementId? CertificateEngagementId);

/// <summary>
/// Result of a successful handshake: the implant's identity, its (confirmed)
/// engagement, and the time presence was recorded. The transport echoes the
/// engagement id back so the implant can confirm its binding.
/// </summary>
public sealed record HandshakeResult(
    ImplantId ImplantId,
    EngagementId EngagementId,
    DateTimeOffset At);
