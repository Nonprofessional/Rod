using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Sessions;

namespace Rod.CoreState.Tests;

/// <summary>
/// The durable last-seen stamp: the entity's NoteSeen semantics (monotonic,
/// one advance per minute) and the registry decorator that drives it from
/// every session Open, Touch, and sweep-close.
/// </summary>
public class LastSeenStampTests
{
    private static readonly EngagementId Engagement = EngagementId.New();

    private static (Implant Implant, IImplantRepository Repo, LastSeenSessionRegistry Registry, TimeProvider Clock)
        FreshAsync(DateTimeOffset now)
    {
        var implant = Implant.Enroll(ImplantId.New(), Engagement, now.AddDays(30), ImplantClass.Implant, now);
        var repo = new InMemoryImplantRepository();
        return (implant, repo, new LastSeenSessionRegistry(new InMemorySessionRegistry(), repo), new FixedTimeProvider(now));
    }

    [Fact]
    public void NoteSeen_FirstStamp_Moves()
    {
        var now = DateTimeOffset.Parse("2026-09-08T10:00:00Z");
        var implant = Implant.Enroll(ImplantId.New(), Engagement, now.AddDays(30), ImplantClass.Implant, now);

        Assert.Null(implant.LastSeenAt);
        Assert.True(implant.NoteSeen(now.AddMinutes(2)));
        Assert.Equal(now.AddMinutes(2), implant.LastSeenAt);
    }

    [Fact]
    public void NoteSeen_WithinTheMinuteWindow_DoesNotMove()
    {
        var now = DateTimeOffset.Parse("2026-09-08T10:00:00Z");
        var implant = Implant.Enroll(ImplantId.New(), Engagement, now.AddDays(30), ImplantClass.Implant, now);
        implant.NoteSeen(now);

        // Fresh but inside the throttle window: no move, no save.
        Assert.False(implant.NoteSeen(now.AddSeconds(30)));
        Assert.Equal(now, implant.LastSeenAt);
    }

    [Fact]
    public void NoteSeen_OlderThanTheStamp_NeverMovesBackwards()
    {
        var now = DateTimeOffset.Parse("2026-09-08T10:00:00Z");
        var implant = Implant.Enroll(ImplantId.New(), Engagement, now.AddDays(30), ImplantClass.Implant, now);
        implant.NoteSeen(now.AddMinutes(5));

        Assert.False(implant.NoteSeen(now));
        Assert.Equal(now.AddMinutes(5), implant.LastSeenAt);
    }

    [Fact]
    public async Task Registry_OpenTouchesAndSweeps_AdvanceTheStamp()
    {
        var start = DateTimeOffset.Parse("2026-09-08T10:00:00Z");
        var (implant, repo, registry, _) = FreshAsync(start);
        await repo.SaveAsync(implant);

        // Open records the first stamp.
        var session = await registry.OpenAsync(implant, Array.Empty<string>(), start.AddSeconds(10));
        var afterOpen = (await repo.FindAsync(implant.Id))!.LastSeenAt;
        Assert.Equal(start.AddSeconds(10), afterOpen);

        // A touch inside the throttle window does not rewrite the row.
        await registry.TouchAsync(implant.Id, Array.Empty<string>(), start.AddSeconds(40));
        Assert.Equal(afterOpen, (await repo.FindAsync(implant.Id))!.LastSeenAt);

        // A touch past the window advances it.
        await registry.TouchAsync(implant.Id, Array.Empty<string>(), start.AddMinutes(2));
        Assert.Equal(start.AddMinutes(2), (await repo.FindAsync(implant.Id))!.LastSeenAt);

        // The staleness sweep closes the silent session and stamps the close.
        await registry.SweepStaleAsync(start.AddMinutes(3), start.AddMinutes(20));
        Assert.Equal(start.AddMinutes(20), (await repo.FindAsync(implant.Id))!.LastSeenAt);
        Assert.Null(await registry.GetActiveAsync(implant.Id));
    }

    [Fact]
    public async Task Registry_CloseAdvancesTheStamp()
    {
        var start = DateTimeOffset.Parse("2026-09-08T10:00:00Z");
        var (implant, repo, registry, _) = FreshAsync(start);
        await repo.SaveAsync(implant);
        var session = await registry.OpenAsync(implant, Array.Empty<string>(), start);

        await registry.CloseAsync(session.Id, start.AddMinutes(5));
        Assert.Equal(start.AddMinutes(5), (await repo.FindAsync(implant.Id))!.LastSeenAt);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
