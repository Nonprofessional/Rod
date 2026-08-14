using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Live;
using Rod.CoreState.Sessions;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of <see cref="SessionSweepService"/> (architecture.md Sec 10.3): the
/// service runs the registry sweep against a cutoff and fans one
/// <see cref="LiveEventKind.SessionClosed"/> event per closed session out to the
/// live bus, so connected operators see the implant drop off the roster.
/// </summary>
public class SessionSweepServiceTests
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

    private static Implant NewImplant(EngagementId engagement)
        => Implant.Enroll(ImplantId.New(), engagement, "key-abc", Now.AddDays(30), ImplantClass.Stage2, Now);

    [Fact]
    public async Task SweepStale_ClosesSilentSessions_AndPublishesOneEventEach()
    {
        var sessions = new InMemorySessionRegistry();
        var bus = new CaptureBus();
        var service = new SessionSweepService(sessions, new FakeClock(Now.AddMinutes(10)), bus);
        var engagement = EngagementId.New();
        var staleImplant = NewImplant(engagement);
        var stale = await sessions.OpenAsync(staleImplant, new[] { "shell.exec" }, Now);

        var closed = await service.SweepStaleAsync(cutoff: Now.AddMinutes(2));

        var swept = Assert.Single(closed);
        Assert.Equal(stale.Id, swept.Id);

        var published = Assert.Single(bus.Events);
        Assert.Equal(LiveEventKind.SessionClosed, published.Kind);
        Assert.Equal(engagement, published.EngagementId);
        Assert.Equal(staleImplant.Id, published.ImplantId);
        Assert.Contains(stale.Id.ToString(), published.Payload);
        Assert.Equal(Now.AddMinutes(10), published.At);
    }

    [Fact]
    public async Task SweepStale_ClosesSessionsAcrossEngagements_ScopedPerEvent()
    {
        // The sweep is global; each closed session's event stays scoped to its
        // own engagement (architecture.md Sec 3) so subscribers never see
        // another engagement's sessions.
        var sessions = new InMemorySessionRegistry();
        var bus = new CaptureBus();
        var service = new SessionSweepService(sessions, new FakeClock(Now.AddMinutes(10)), bus);
        var engagementA = EngagementId.New();
        var engagementB = EngagementId.New();
        await sessions.OpenAsync(NewImplant(engagementA), Array.Empty<string>(), Now);
        await sessions.OpenAsync(NewImplant(engagementB), Array.Empty<string>(), Now);

        var closed = await service.SweepStaleAsync(cutoff: Now.AddMinutes(2));

        Assert.Equal(2, closed.Count);
        Assert.Equal(2, bus.Events.Count);
        Assert.Equal(
            new[] { engagementA, engagementB }.OrderBy(e => e.Value).ToArray(),
            bus.Events.Select(e => e.EngagementId).OrderBy(e => e.Value).ToArray());
    }

    [Fact]
    public async Task SweepStale_WithoutABus_StillClosesSessions()
    {
        // The bus is optional (the core-state unit test constructor); its absence
        // skips the fan-out but never the sweep itself.
        var sessions = new InMemorySessionRegistry();
        var service = new SessionSweepService(sessions, new FakeClock(Now.AddMinutes(10)));
        await sessions.OpenAsync(NewImplant(EngagementId.New()), Array.Empty<string>(), Now);

        var closed = await service.SweepStaleAsync(cutoff: Now.AddMinutes(2));

        Assert.Single(closed);
    }
}
