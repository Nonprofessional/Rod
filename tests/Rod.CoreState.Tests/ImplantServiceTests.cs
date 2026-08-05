using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Sessions;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of <see cref="ImplantService"/>'s retire use case (architecture.md
/// Sec 7, M4.4). Retiring an implant marks it retired, closes its active
/// session, and publishes an <see cref="LiveEventKind.ImplantRetired"/> event on
/// the live bus so connected operators see the implant leave the live fleet.
/// Drives the service against the in-memory ports the rest of the core-state
/// tests use, with a capturing bus so the fan-out is asserted directly.
/// </summary>
public class ImplantServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    // Captures every published event; exposes a single subscriber stream.
    private sealed class CaptureBus : ILiveEventBus
    {
        public List<LiveEvent> Events { get; } = new();

        public Task PublishAsync(LiveEvent @event, CancellationToken cancellationToken = default)
        {
            Events.Add(@event);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<LiveEvent> SubscribeAsync(
            EngagementId engagement,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static async Task<Implant> EnrollAsync(
        InMemoryImplantRepository implants, EngagementId engagement)
    {
        var implant = Implant.Enroll(
            ImplantId.New(), engagement, "key-abc", Now.AddDays(30), ImplantClass.Stage2, Now);
        await implants.SaveAsync(implant);
        return implant;
    }

    [Fact]
    public async Task RetireAsync_MarksRetired_ClosesActiveSession_AndPublishesLive()
    {
        var implants = new InMemoryImplantRepository();
        var sessions = new InMemorySessionRegistry();
        var bus = new CaptureBus();
        var engagement = EngagementId.New();
        var implant = await EnrollAsync(implants, engagement);

        // Give the implant an active session, so retire has one to close.
        await sessions.OpenAsync(implant, new[] { "shell.exec" }, Now);

        var service = new ImplantService(implants, sessions, new FakeClock(Now), bus);
        var retiredBy = OperatorId.New();

        var result = await service.RetireAsync(
            new RetireImplantCommand(engagement, implant.Id, retiredBy));

        Assert.Equal(implant.Id, result.ImplantId);
        Assert.Equal(engagement, result.EngagementId);
        Assert.Equal(retiredBy, result.RetiredBy);
        Assert.True(result.JustRetired);
        Assert.NotNull(result.ClosedSession);

        // The implant is now retired; its active session is gone.
        Assert.True(implant.IsRetired);
        Assert.Null(await sessions.GetActiveAsync(implant.Id));

        // The live fan-out fired exactly one ImplantRetired event on the
        // engagement, carrying the implant and the retiring operator.
        var live = Assert.Single(bus.Events);
        Assert.Equal(LiveEventKind.ImplantRetired, live.Kind);
        Assert.Equal(engagement, live.EngagementId);
        Assert.Equal(implant.Id, live.ImplantId);
        Assert.Equal(retiredBy, live.OperatorId);
    }

    [Fact]
    public async Task RetireAsync_OnAlreadyRetiredImplant_IsANoOp()
    {
        var implants = new InMemoryImplantRepository();
        var sessions = new InMemorySessionRegistry();
        var bus = new CaptureBus();
        var engagement = EngagementId.New();
        var implant = await EnrollAsync(implants, engagement);
        var firstAt = Now.AddSeconds(1);

        var service = new ImplantService(implants, sessions, new FakeClock(firstAt), bus);

        await service.RetireAsync(new RetireImplantCommand(engagement, implant.Id, OperatorId.New()));
        var second = await service.RetireAsync(new RetireImplantCommand(engagement, implant.Id, OperatorId.New()));

        // The second call did not retire again: JustRetired is false and
        // RetiredAt is unchanged. The fan-out still fires for the duplicate
        // (every operator action is observable), so two events were published.
        Assert.False(second.JustRetired);
        Assert.Equal(firstAt, second.RetiredAt);
        Assert.Equal(2, bus.Events.Count);
    }

    [Fact]
    public async Task RetireAsync_OnOfflineImplant_DoesNotCloseASession()
    {
        var implants = new InMemoryImplantRepository();
        var sessions = new InMemorySessionRegistry();
        var engagement = EngagementId.New();
        var implant = await EnrollAsync(implants, engagement);

        var service = new ImplantService(implants, sessions, new FakeClock(Now));
        var result = await service.RetireAsync(
            new RetireImplantCommand(engagement, implant.Id, OperatorId.New()));

        // The implant was offline, so there was no session to close.
        Assert.Null(result.ClosedSession);
        Assert.True(implant.IsRetired);
    }

    [Fact]
    public async Task RetireAsync_RefusesForeignEngagement()
    {
        var implants = new InMemoryImplantRepository();
        var sessions = new InMemorySessionRegistry();
        var implant = await EnrollAsync(implants, EngagementId.New());

        var service = new ImplantService(implants, sessions, new FakeClock(Now));

        await Assert.ThrowsAsync<ImplantNotFoundException>(() => service.RetireAsync(
            new RetireImplantCommand(EngagementId.New(), implant.Id, OperatorId.New())));
    }
}
