using Rod.CoreState.Live;
using Rod.CoreState.Sessions;

namespace Rod.CoreState.Application;

/// <summary>
/// The session staleness use case (architecture.md Sec 10.3): close every
/// session whose beacon stream has gone silent past a cutoff. A stream that dies
/// without a clean close -- the implant vanishes mid-stream, or the connection
/// drops silently -- leaves its session Active forever; the transport's hosted
/// sweeper drives this on a configured threshold, and each close fans out a
/// <see cref="LiveEventKind.SessionClosed"/> event so connected operators see
/// the implant drop off the online roster. The bus is optional at the
/// constructor (the core-state unit tests construct the service without one) and
/// its absence simply skips the publish.
/// </summary>
/// <remarks>
/// The sweep itself is registry-owned (see
/// <see cref="ISessionRegistry.SweepStaleAsync"/>); this service is the
/// composition point -- registry, clock, and live bus -- the transport layer's
/// hosted service calls once per sweep interval.
/// </remarks>
public sealed class SessionSweepService
{
    private readonly ISessionRegistry _sessions;
    private readonly TimeProvider _clock;
    private readonly ILiveEventBus? _bus;

    public SessionSweepService(ISessionRegistry sessions, TimeProvider clock)
        : this(sessions, clock, bus: null)
    {
    }

    /// <summary>
    /// Constructs the service with a live-event bus. The composition root wires
    /// the bus; the two-argument constructor keeps the core-state unit tests
    /// bus-free, mirroring <see cref="TaskService"/>.
    /// </summary>
    public SessionSweepService(ISessionRegistry sessions, TimeProvider clock, ILiveEventBus? bus)
    {
        _sessions = sessions;
        _clock = clock;
        _bus = bus;
    }

    /// <summary>
    /// Closes every Active session whose last-seen stamp is older than
    /// <paramref name="cutoff"/>, publishing a
    /// <see cref="LiveEventKind.SessionClosed"/> event per closed session.
    /// Returns the closed sessions so callers (tests, log surfaces) can observe
    /// what the sweep did.
    /// </summary>
    public async System.Threading.Tasks.Task<IReadOnlyList<Session>> SweepStaleAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var closed = await _sessions.SweepStaleAsync(cutoff, now, cancellationToken);

        if (_bus is not null)
        {
            foreach (var session in closed)
            {
                await _bus.PublishAsync(
                    LiveEvent.SessionClosed(
                        session.EngagementId,
                        session.ImplantId,
                        payload: $"Session {session.Id} swept: last seen {session.LastSeenAt:O}, " +
                            $"silent for {now - session.LastSeenAt}.",
                        now),
                    cancellationToken);
            }
        }

        return closed;
    }
}
