using System.Collections.Concurrent;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Live;

namespace Rod.CoreState.Operators.Interaction;

/// <summary>
/// The engagement-scoped registry of interaction ownership (architecture.md
/// Sec 4.5): the exclusive claims on the typing halves -- a live channel
/// task's input and relay binds, a caught shell's input -- and the
/// per-implant activity presence that marks who is driving what. Ephemeral
/// state beside the presence roster: process-local, rebuilt from nothing on
/// a restart, visible on the live bus, and never an audit fact -- the
/// per-input events the input routes already write are the attributed
/// record.
///
/// Lives in core state because both sides of it are transport-facing: the
/// input routes enforce the claims and note the activity, while the
/// operator layer's event stream seeds its hello frame and releases every
/// claim an operator holds when their connection ends -- the layer rule
/// keeps the service where both can reach it.
/// </summary>
public sealed class OperatorInteractionService
{
    private readonly ILiveEventBus _bus;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<EngagementId, EngagementState> _engagements = new();

    public OperatorInteractionService(ILiveEventBus bus, TimeProvider clock)
    {
        _bus = bus;
        _clock = clock;
    }

    /// <summary>
    /// Takes the claim on an interaction surface. Idempotent for the current
    /// holder (the same claim back, its acquisition time untouched); refused
    /// with the holder's claim when another operator holds it. Publishes
    /// <see cref="LiveEventKind.ClaimAcquired"/> when the claim changes hands
    /// from unclaimed -- the idempotent re-acquire of a holder publishes
    /// nothing, so a console's heartbeat cannot become a firehose.
    /// </summary>
    public async Task<InteractionAcquireResult> TryAcquireAsync(
        EngagementId engagement,
        InteractionSurface surface,
        Guid surfaceId,
        OperatorId @operator,
        CancellationToken cancellationToken = default)
    {
        var state = _engagements.GetOrAdd(engagement, _ => new EngagementState());
        var at = _clock.GetUtcNow();

        InteractionAcquireResult result;
        bool acquired;
        lock (state.Gate)
        {
            var key = new SurfaceKey(surface, surfaceId);
            if (state.Claims.TryGetValue(key, out var held))
            {
                if (held.OperatorId == @operator)
                    return InteractionAcquireResult.Taken(held);
                return InteractionAcquireResult.Refused(held);
            }

            var claim = new InteractionClaim(engagement, surface, surfaceId, @operator, at);
            state.Claims[key] = claim;
            result = InteractionAcquireResult.Taken(claim);
            acquired = true;
        }

        if (acquired)
            await PublishClaimAsync(engagement, LiveEventKind.ClaimAcquired, result.Claim!, cancellationToken);
        return result;
    }

    /// <summary>
    /// Releases the claim, holder-only: an operator cannot drop a peer's
    /// claim (teardown of the surface itself -- closing a shell, completing a
    /// channel -- is the safety valve that never needed the claim). Publishes
    /// <see cref="LiveEventKind.ClaimReleased"/> when the release landed.
    /// </summary>
    public async Task<bool> ReleaseAsync(
        EngagementId engagement,
        InteractionSurface surface,
        Guid surfaceId,
        OperatorId @operator,
        CancellationToken cancellationToken = default)
    {
        var state = _engagements.GetOrAdd(engagement, _ => new EngagementState());

        InteractionClaim? released;
        lock (state.Gate)
        {
            var key = new SurfaceKey(surface, surfaceId);
            if (!state.Claims.TryGetValue(key, out var held) || held.OperatorId != @operator)
                return false;
            state.Claims.Remove(key);
            released = held;
        }

        await PublishClaimAsync(engagement, LiveEventKind.ClaimReleased, released!, cancellationToken);
        return true;
    }

    /// <summary>
    /// Drops a claim regardless of holder -- the lazy cleanup the claims
    /// listing performs for surfaces that ended (a completed channel, a closed
    /// shell). The dropped claim was inert: the surface's own state refused
    /// input before the claim was consulted. Publishes the release so peers'
    /// views do not linger on a dead surface.
    /// </summary>
    public async Task<bool> DropAsync(
        EngagementId engagement,
        InteractionSurface surface,
        Guid surfaceId,
        CancellationToken cancellationToken = default)
    {
        var state = _engagements.GetOrAdd(engagement, _ => new EngagementState());

        InteractionClaim? dropped;
        lock (state.Gate)
        {
            var key = new SurfaceKey(surface, surfaceId);
            if (!state.Claims.Remove(key, out var held))
                return false;
            dropped = held;
        }

        await PublishClaimAsync(engagement, LiveEventKind.ClaimReleased, dropped!, cancellationToken);
        return true;
    }

    /// <summary>
    /// Releases every claim the operator holds on the engagement -- the
    /// disconnect path the event stream's close performs (architecture.md
    /// Sec 4.5): a claim is only as durable as its holder's connection.
    /// Returns the released claims so the caller can report them.
    /// </summary>
    public async Task<IReadOnlyList<InteractionClaim>> ReleaseAllForOperatorAsync(
        EngagementId engagement,
        OperatorId @operator,
        CancellationToken cancellationToken = default)
    {
        var state = _engagements.GetOrAdd(engagement, _ => new EngagementState());

        List<InteractionClaim> released;
        lock (state.Gate)
        {
            released = state.Claims.Values.Where(c => c.OperatorId == @operator).ToList();
            foreach (var claim in released)
                state.Claims.Remove(new SurfaceKey(claim.Surface, claim.SurfaceId));
        }

        foreach (var claim in released)
            await PublishClaimAsync(engagement, LiveEventKind.ClaimReleased, claim, cancellationToken);
        return released;
    }

    /// <summary>The engagement's held claims -- the visibility half of ownership.</summary>
    public Task<IReadOnlyList<InteractionClaim>> ListAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        if (!_engagements.TryGetValue(engagement, out var state))
            return Task.FromResult<IReadOnlyList<InteractionClaim>>(Array.Empty<InteractionClaim>());

        lock (state.Gate)
            return Task.FromResult<IReadOnlyList<InteractionClaim>>(state.Claims.Values.ToArray());
    }

    /// <summary>
    /// Notes tasking activity against an implant: the operator's issuance or
    /// input post marks them as driving it. A refresh by the current driver
    /// publishes nothing; a change of driver publishes one
    /// <see cref="LiveEventKind.ImplantActivity"/> event -- a hand-off is one
    /// beat, and tasking cadence never becomes a firehose.
    /// </summary>
    public async Task NoteActivityAsync(
        EngagementId engagement,
        ImplantId implant,
        OperatorId @operator,
        CancellationToken cancellationToken = default)
    {
        var state = _engagements.GetOrAdd(engagement, _ => new EngagementState());
        var at = _clock.GetUtcNow();

        bool handedOff;
        lock (state.Gate)
        {
            if (state.Driving.TryGetValue(implant, out var current) && current.OperatorId == @operator)
            {
                // The same driver refreshes in place -- last-seen moves, no event.
                state.Driving[implant] = new InteractionActivity(engagement, implant, @operator, at);
                return;
            }

            state.Driving[implant] = new InteractionActivity(engagement, implant, @operator, at);
            handedOff = true;
        }

        if (handedOff)
        {
            await _bus.PublishAsync(
                LiveEvent.ImplantActivity(engagement, @operator, implant, at.ToString("O"), at),
                cancellationToken);
        }
    }

    /// <summary>The engagement's driving map -- which operator each worked implant last saw.</summary>
    public Task<IReadOnlyList<InteractionActivity>> ListDrivingAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        if (!_engagements.TryGetValue(engagement, out var state))
            return Task.FromResult<IReadOnlyList<InteractionActivity>>(Array.Empty<InteractionActivity>());

        lock (state.Gate)
            return Task.FromResult<IReadOnlyList<InteractionActivity>>(state.Driving.Values.ToArray());
    }

    private Task PublishClaimAsync(
        EngagementId engagement,
        LiveEventKind kind,
        InteractionClaim claim,
        CancellationToken cancellationToken)
        => _bus.PublishAsync(
            LiveEvent.Claim(engagement, kind, claim.OperatorId, claim.Surface, claim.SurfaceId, claim.AcquiredAt),
            cancellationToken);

    // Claims are keyed by surface: one holder per (surface kind, surface id)
    // pair, engagement-scoped by the state object the pair lives in.
    private readonly record struct SurfaceKey(InteractionSurface Surface, Guid SurfaceId);

    // One engagement's ownership state. The gate serializes every mutation so
    // the acquire/release/release-all transitions are atomic against each
    // other -- two operators racing to claim resolve one winner, never both.
    private sealed class EngagementState
    {
        public Lock Gate { get; } = new();

        public Dictionary<SurfaceKey, InteractionClaim> Claims { get; } = new();

        public Dictionary<ImplantId, InteractionActivity> Driving { get; } = new();
    }
}
