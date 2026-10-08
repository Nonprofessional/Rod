using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Operators.Interaction;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of <see cref="OperatorInteractionService"/> (architecture.md
/// Sec 4.5): the exclusive claims on interaction surfaces -- one holder,
/// idempotent re-acquire, holder-only release, the disconnect cleanup, and
/// the lazy drop -- and the per-implant activity presence that publishes one
/// beat on a driver hand-off and nothing on a refresh.
/// </summary>
public class OperatorInteractionServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void AdvanceTo(DateTimeOffset now) => _now = now;
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

    [Fact]
    public async Task Claim_IsExclusive_RefusedWithTheHolder()
    {
        var bus = new CaptureBus();
        var clock = new FakeClock(Now);
        var service = new OperatorInteractionService(bus, clock);
        var engagement = EngagementId.New();
        var surfaceId = Guid.NewGuid();
        var alice = OperatorId.New();
        var bob = OperatorId.New();

        var taken = await service.TryAcquireAsync(engagement, InteractionSurface.ChannelTask, surfaceId, alice);
        Assert.True(taken.Acquired);
        Assert.Equal(alice, taken.Claim!.OperatorId);
        Assert.Equal(Now, taken.Claim.AcquiredAt);

        var refused = await service.TryAcquireAsync(engagement, InteractionSurface.ChannelTask, surfaceId, bob);
        Assert.False(refused.Acquired);
        Assert.Equal(alice, refused.HeldBy!.OperatorId);

        // Exactly one acquire beat for the surface -- the refusal the second
        // operator met publishes nothing of its own.
        var acquired = Assert.Single(
            bus.Events,
            e => e.Kind == LiveEventKind.ClaimAcquired);
        Assert.Equal(alice, acquired.OperatorId);
        Assert.Equal(new TaskId(surfaceId), acquired.TaskId);
        Assert.Equal($"channel/{InteractionClaim.WireId(InteractionSurface.ChannelTask, surfaceId)}", acquired.Payload);
    }

    [Fact]
    public async Task Reacquire_ByTheHolder_IsIdempotent()
    {
        var bus = new CaptureBus();
        var clock = new FakeClock(Now);
        var service = new OperatorInteractionService(bus, clock);
        var engagement = EngagementId.New();
        var surfaceId = Guid.NewGuid();
        var alice = OperatorId.New();

        await service.TryAcquireAsync(engagement, InteractionSurface.ShellSession, surfaceId, alice);

        clock.AdvanceTo(Now.AddMinutes(5));
        var again = await service.TryAcquireAsync(engagement, InteractionSurface.ShellSession, surfaceId, alice);

        Assert.True(again.Acquired);
        Assert.Equal(Now, again.Claim!.AcquiredAt); // untouched, not refreshed
        Assert.Single(bus.Events, e => e.Kind == LiveEventKind.ClaimAcquired);
    }

    [Fact]
    public async Task Release_IsHolderOnly_AndPublishes()
    {
        var bus = new CaptureBus();
        var service = new OperatorInteractionService(bus, new FakeClock(Now));
        var engagement = EngagementId.New();
        var surfaceId = Guid.NewGuid();
        var alice = OperatorId.New();
        var bob = OperatorId.New();

        await service.TryAcquireAsync(engagement, InteractionSurface.ShellSession, surfaceId, alice);

        // A peer cannot drop the holder's claim.
        Assert.False(await service.ReleaseAsync(engagement, InteractionSurface.ShellSession, surfaceId, bob));

        Assert.True(await service.ReleaseAsync(engagement, InteractionSurface.ShellSession, surfaceId, alice));
        Assert.Empty(await service.ListAsync(engagement));

        var released = Assert.Single(
            bus.Events,
            e => e.Kind == LiveEventKind.ClaimReleased);
        Assert.Equal(alice, released.OperatorId);
        Assert.Null(released.TaskId); // a shell claim carries no task

        // The surface is claimable again after the release.
        var retaken = await service.TryAcquireAsync(engagement, InteractionSurface.ShellSession, surfaceId, bob);
        Assert.True(retaken.Acquired);
    }

    [Fact]
    public async Task ReleaseAllForOperator_DropsOnlyTheLeaver()
    {
        var bus = new CaptureBus();
        var service = new OperatorInteractionService(bus, new FakeClock(Now));
        var engagement = EngagementId.New();
        var alice = OperatorId.New();
        var bob = OperatorId.New();
        var shell = Guid.NewGuid();
        var channel = Guid.NewGuid();

        await service.TryAcquireAsync(engagement, InteractionSurface.ShellSession, shell, alice);
        await service.TryAcquireAsync(engagement, InteractionSurface.ChannelTask, channel, alice);
        await service.TryAcquireAsync(engagement, InteractionSurface.ShellSession, Guid.NewGuid(), bob);

        var released = await service.ReleaseAllForOperatorAsync(engagement, alice);

        Assert.Equal(2, released.Count);
        var remaining = await service.ListAsync(engagement);
        var held = Assert.Single(remaining);
        Assert.Equal(bob, held.OperatorId);
        Assert.Equal(2, bus.Events.Count(e => e.Kind == LiveEventKind.ClaimReleased));
    }

    [Fact]
    public async Task Claims_AreEngagementScoped()
    {
        var service = new OperatorInteractionService(new CaptureBus(), new FakeClock(Now));
        var one = EngagementId.New();
        var two = EngagementId.New();
        var surfaceId = Guid.NewGuid();
        var alice = OperatorId.New();
        var bob = OperatorId.New();

        await service.TryAcquireAsync(one, InteractionSurface.ChannelTask, surfaceId, alice);

        // The same surface id under another engagement is another surface
        // entirely (ids are only unique within an engagement).
        var other = await service.TryAcquireAsync(two, InteractionSurface.ChannelTask, surfaceId, bob);
        Assert.True(other.Acquired);
    }

    [Fact]
    public async Task Activity_PublishesOnHandOff_SilencesOnRefresh()
    {
        var bus = new CaptureBus();
        var service = new OperatorInteractionService(bus, new FakeClock(Now));
        var engagement = EngagementId.New();
        var implant = ImplantId.New();
        var alice = OperatorId.New();
        var bob = OperatorId.New();

        await service.NoteActivityAsync(engagement, implant, alice);
        await service.NoteActivityAsync(engagement, implant, alice);

        var first = Assert.Single(bus.Events);
        Assert.Equal(LiveEventKind.ImplantActivity, first.Kind);
        Assert.Equal(alice, first.OperatorId);
        Assert.Equal(implant, first.ImplantId);

        await service.NoteActivityAsync(engagement, implant, bob);

        Assert.Equal(2, bus.Events.Count);
        Assert.Equal(bob, bus.Events[1].OperatorId);

        var driving = Assert.Single(await service.ListDrivingAsync(engagement));
        Assert.Equal(bob, driving.OperatorId);
        Assert.Equal(implant, driving.ImplantId);
    }
}

/// <summary>
/// Checks of the scope model (architecture.md Sec 4.5): the claim-string
/// round trip, the assignment validation that keeps acting and approving
/// grounded in reading, and the entity's default -- every provisioned
/// operator holds the full peer set until someone narrows it.
/// </summary>
public class OperatorScopeTests
{
    [Fact]
    public void ClaimValue_RoundTrips()
    {
        foreach (var scopes in new[]
                 {
                     OperatorScope.All,
                     OperatorScope.Read,
                     OperatorScope.Read | OperatorScope.Task,
                     OperatorScope.Read | OperatorScope.Approve,
                     OperatorScope.None,
                 })
        {
            Assert.Equal(scopes, OperatorScopes.FromClaimValue(OperatorScopes.ToClaimValue(scopes)));
        }

        Assert.Equal("read,task,approve", OperatorScopes.ToClaimValue(OperatorScope.All));
        // An unknown name (a scope a newer server stamps) degrades to the
        // scopes this one understands instead of poisoning the set.
        Assert.Equal(OperatorScope.Read, OperatorScopes.FromClaimValue("read,nope"));
    }

    [Fact]
    public void Validation_RequiresReadBeneathTaskAndApprove()
    {
        Assert.Null(OperatorScopes.Validate(OperatorScope.All));
        Assert.Null(OperatorScopes.Validate(OperatorScope.Read));
        Assert.Null(OperatorScopes.Validate(OperatorScope.None));

        Assert.NotNull(OperatorScopes.Validate(OperatorScope.Task));
        Assert.NotNull(OperatorScopes.Validate(OperatorScope.Approve));
        Assert.NotNull(OperatorScopes.Validate(OperatorScope.Task | OperatorScope.Approve));
    }

    [Fact]
    public void Operator_DefaultsToThePeerSet_AndReplacesItsScopes()
    {
        var registered = Operator.Register(OperatorId.New(), "alice", "Alice", DateTimeOffset.UnixEpoch);
        Assert.Equal(OperatorScope.All, registered.Scopes);

        var narrowed = registered.WithScopes(OperatorScope.Read);
        Assert.Equal(OperatorScope.Read, narrowed.Scopes);
        Assert.Equal(registered.Id, narrowed.Id);
        Assert.Equal(registered.Handle, narrowed.Handle);
        Assert.Equal(registered.CreatedAt, narrowed.CreatedAt);

        Assert.Throws<ArgumentException>(
            () => registered.WithScopes((OperatorScope)99));
    }
}
