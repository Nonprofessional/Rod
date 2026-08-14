using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Live;
using Rod.CoreState.Sessions;

namespace Rod.CoreState.Application;

/// <summary>
/// The implant lifecycle use case beyond enrollment: retiring an implant
/// (architecture.md Sec 7, ). Taking an implant out of operation marks it
/// retired -- so it is refused at handshake and untaskable thereafter (the
/// gates live in <see cref="HandshakeService"/> and <see cref="TaskService"/>)
/// -- and closes its active session, so a connected implant is dropped from the
/// live fleet the moment it is retired rather than on its next (refused)
/// handshake. Orchestrates the core-state ports; holds no state of its own.
///
/// Audit-agnostic by design: the transport layer composes the
/// <c>ImplantRetired</c> audit write (architecture.md Sec 11), the same way it
/// composes the task-completion write on the beacon stream and the PayloadBuilt
/// write at the build endpoint. The live bus is optional: the core-state unit
/// tests construct this service without one, and the absence simply skips the
/// fan-out (the retire itself is the source of truth; the bus is the transient
/// projection). Mirrors <see cref="TaskService"/>.
///
/// Engagement binding is enforced here (architecture.md Sec 3): retiring an
/// implant from another engagement is impossible by construction. The active
/// session is closed after the retire is persisted, so a retire that fails
/// partway never leaves a half-retired implant.
/// </summary>
public sealed class ImplantService
{
    private readonly IImplantRepository _implants;
    private readonly ISessionRegistry _sessions;
    private readonly TimeProvider _clock;
    private readonly ILiveEventBus? _bus;

    public ImplantService(
        IImplantRepository implants,
        ISessionRegistry sessions,
        TimeProvider clock)
        : this(implants, sessions, clock, bus: null)
    {
    }

    /// <summary>
    /// Constructs the service with a live-event bus. The composition root wires
    /// the bus (); the three-argument constructor above keeps the
    /// core-state unit tests bus-free.
    /// </summary>
    public ImplantService(
        IImplantRepository implants,
        ISessionRegistry sessions,
        TimeProvider clock,
        ILiveEventBus? bus)
    {
        _implants = implants;
        _sessions = sessions;
        _clock = clock;
        _bus = bus;
    }

    /// <summary>
    /// Retires <paramref name="command.ImplantId"/> in
    /// <paramref name="command.EngagementId"/>, attributed to
    /// <paramref name="command.RetiredBy"/>. Marks the implant retired, closes
    /// its active session if it had one, and returns the retire result the
    /// transport turns into an audit event and a live fan-out. A second retire
    /// of an already-retired implant returns <see cref="ImplantRetired.AlreadyRetired"/>;
    /// the implant and its audit record are unchanged, so a duplicate retire is
    /// safe to repeat.
    /// </summary>
    public async Task<ImplantRetired> RetireAsync(
        RetireImplantCommand command,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var implant = await _implants.FindAsync(command.ImplantId, cancellationToken);
        if (implant is null)
        {
            throw new ImplantNotFoundException($"Implant {command.ImplantId} is not enrolled.");
        }
        if (implant.EngagementId != command.EngagementId)
        {
            throw new ImplantNotFoundException(
                $"Implant {implant.Id} belongs to engagement {implant.EngagementId}, " +
                $"not {command.EngagementId}.");
        }

        var justRetired = implant.Retire(now);
        await _implants.SaveAsync(implant, cancellationToken);

        // Close the active session, if any, so a connected implant leaves the
        // live fleet immediately rather than on its next (refused) handshake.
        // No session is the common case -- the implant was offline.
        var active = await _sessions.GetActiveAsync(implant.Id, cancellationToken);
        if (active is not null)
            await _sessions.CloseAsync(active.Id, now, cancellationToken);

        if (_bus is not null)
        {
            await _bus.PublishAsync(
                LiveEvent.ImplantRetired(
                    implant.EngagementId,
                    command.RetiredBy,
                    implant.Id,
                    payload: justRetired ? "retired" : "already retired",
                    now),
                cancellationToken);
        }

        return new ImplantRetired(
            implant.Id,
            implant.EngagementId,
            command.RetiredBy,
            implant.RetiredAt!.Value,
            justRetired,
            active?.Id);
    }
}

/// <summary>
/// Request to retire an implant. <see cref="EngagementId"/> scopes it;
/// <see cref="RetiredBy"/> attributes it; <see cref="ImplantId"/> is the
/// implant being taken out of operation.
/// </summary>
public sealed record RetireImplantCommand(
    EngagementId EngagementId,
    ImplantId ImplantId,
    OperatorId RetiredBy);

/// <summary>
/// Result of retiring an implant: its identity, its (confirmed) engagement, the
/// retiring operator, the recorded retirement timestamp, whether this call was
/// the one that retired it (versus a duplicate on an already-retired implant),
/// and the session that was closed -- null when the implant was offline. This is
/// what the transport turns into an audit event and a live fan-out.
/// </summary>
public sealed record ImplantRetired(
    ImplantId ImplantId,
    EngagementId EngagementId,
    OperatorId RetiredBy,
    DateTimeOffset RetiredAt,
    bool JustRetired,
    SessionId? ClosedSession);

/// <summary>
/// An implant lookup failed -- the id matched no enrolled implant, or the
/// implant belongs to a different engagement than the request
/// (architecture.md Sec 3). The transport maps this to a 404, the same as the
/// implant-listing and task endpoints do for a foreign implant.
/// </summary>
public sealed class ImplantNotFoundException : InvalidOperationException
{
    public ImplantNotFoundException(string message)
        : base(message)
    {
    }
}
