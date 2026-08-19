using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
// Pin Task to the BCL type (the TaskQueueHistoryTests convention): this file
// is async-heavy, so the domain entity is reached by its full name.
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// The fronting claim's core-state half (architecture.md Sec 5.2, the Pivot
/// class): which implants a parent fronts, how the widened claim orders work
/// across the fronting set, and that issuance to a pivot child wakes the
/// parent's writer -- the child has no stream of its own to wake. The
/// transport-side round-trip (the marked frame, the input routing) lives in
/// the integration suite.
/// </summary>
public class FrontedPivotClaimTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task FrontedSet_ReturnsOnlyTheParentsPivotChildren()
    {
        var implants = new InMemoryImplantRepository();
        var engagement = EngagementId.New();
        var parent = await EnrollAsync(implants, engagement, ImplantClass.Stage2, parent: null);
        var pivotChild = await EnrollAsync(implants, engagement, ImplantClass.Pivot, parent: parent);
        var stage2Child = await EnrollAsync(implants, engagement, ImplantClass.Stage2, parent: parent);
        var otherParent = await EnrollAsync(implants, engagement, ImplantClass.Stage2, parent: null);
        var foreignPivot = await EnrollAsync(implants, engagement, ImplantClass.Pivot, parent: otherParent);

        var fronted = await implants.ListFrontedPivotsAsync(parent);

        // The parent fronts its Pivot child exactly: a Stage-2 child runs its
        // own process, and another parent's pivot is not this one's to front.
        var frontedIds = fronted.Select(i => i.Id).ToArray();
        Assert.Equal([pivotChild], frontedIds);
        Assert.DoesNotContain(stage2Child, frontedIds);
        Assert.DoesNotContain(foreignPivot, frontedIds);
    }

    [Fact]
    public async Task FrontedClaim_TakesTheOldestAcrossTheFrontingSet()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var parent = ImplantId.New();
        var pivotChild = ImplantId.New();
        var targets = new[] { parent, pivotChild };

        // Issued in this order: the child's task first, then the parent's --
        // FIFO across the whole fronting set by enqueue order, not grouped by
        // target.
        var childTask = Issue(engagement, pivotChild, Now);
        var parentTask = Issue(engagement, parent, Now.AddSeconds(1));
        await tasks.SaveAsync(childTask);
        await tasks.SaveAsync(parentTask);

        var first = await tasks.ClaimNextPendingForAsync(targets, Now.AddSeconds(2));
        var second = await tasks.ClaimNextPendingForAsync(targets, Now.AddSeconds(3));
        var drained = await tasks.ClaimNextPendingForAsync(targets, Now.AddSeconds(4));

        Assert.Equal(childTask.Id, first!.Id);
        Assert.Equal(parentTask.Id, second!.Id);
        Assert.Null(drained);
    }

    [Fact]
    public async Task NarrowClaim_LeavesFrontedTaskingForAStream()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var parent = ImplantId.New();
        var pivotChild = ImplantId.New();
        var childTask = Issue(engagement, pivotChild, Now);
        await tasks.SaveAsync(childTask);

        // The narrow claim the parent's poll transport makes (DNS, an envelope
        // check-in) cannot carry a fronted channel's input half, so it must
        // not claim the child's tasking -- it parks for the fronting stream.
        // (The child itself never checks in at all, so nothing else claims it.)
        Assert.Null(await tasks.ClaimNextPendingAsync(parent, Now.AddSeconds(1)));

        var fronted = await tasks.ClaimNextPendingForAsync([parent, pivotChild], Now.AddSeconds(3));
        Assert.Equal(childTask.Id, fronted!.Id);
    }

    [Fact]
    public async Task IssueToPivotChild_WakesTheFrontingParent()
    {
        var h = await HarnessAsync();
        var pivotChild = await EnrollAsync(h.Implants, h.Engagement, ImplantClass.Pivot, parent: h.Parent);

        // The parent's writer parks on the parent's wake (architecture.md
        // Sec 10.3); the child has no stream, so its task's wake must reach
        // the parent or the fronted task parks queued until an unrelated
        // claim happens.
        var waiting = h.Wake.WaitAsync(h.Parent);
        Assert.False(waiting.IsCompleted);

        await h.Service.IssueAsync(new IssueTaskCommand(
            h.Engagement, pivotChild, OperatorId.New(), "tunnel.forward", "host.example 443"));

        await waiting;
    }

    [Fact]
    public async Task FrontedDispatch_ClaimsTheChildsTask_WithoutANonce()
    {
        var h = await HarnessAsync();
        var pivotChild = await EnrollAsync(h.Implants, h.Engagement, ImplantClass.Pivot, parent: h.Parent);
        await h.Service.IssueAsync(new IssueTaskCommand(
            h.Engagement, pivotChild, OperatorId.New(), "tunnel.forward", "host.example 443"));

        // The narrow claim sees nothing (the child is not the caller); the
        // fronting claim hands the child's task to the parent's writer, with
        // no nonce -- the child never handshakes, so it never negotiated the
        // replay-nonce arm and its tasking keeps the four-element tuple
        // (architecture.md Sec 9).
        Assert.Null(await h.Service.DispatchNextAsync(h.Parent));
        var fronted = await h.Service.DispatchNextAsync(h.Parent, includeFronted: true);
        Assert.NotNull(fronted);
        Assert.Equal(pivotChild, fronted!.ImplantId);
        Assert.Equal("tunnel.forward", fronted.Verb);
        Assert.Null(fronted.Nonce);
    }

    private static async Task<ImplantId> EnrollAsync(
        IImplantRepository implants,
        EngagementId engagement,
        ImplantClass @class,
        ImplantId? parent)
    {
        var id = ImplantId.New();
        var implant = parent is null
            ? Implant.Enroll(id, engagement, Now.AddDays(30), @class, Now)
            : Implant.EnrollChild(id, engagement, Now.AddDays(30), @class, Now, parentImplantId: parent);
        await implants.SaveAsync(implant);
        return id;
    }

    private static Rod.CoreState.Tasks.Task Issue(
        EngagementId engagement,
        ImplantId implant,
        DateTimeOffset at)
        => Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), engagement, implant, OperatorId.New(), "tunnel.forward", "host.example 443", at);

    private sealed record Harness(
        EngagementId Engagement,
        ImplantId Parent,
        IImplantRepository Implants,
        TaskService Service,
        InMemoryTaskDispatchWake Wake);

    private static async Task<Harness> HarnessAsync()
    {
        var engagements = new InMemoryEngagementRepository();
        var engagement = Engagement.Create(EngagementId.New(), "fronting-test", OperatorId.New(), Now);
        await engagements.SaveAsync(engagement);

        var implants = new InMemoryImplantRepository();
        var parent = await EnrollAsync(implants, engagement.Id, ImplantClass.Stage2, parent: null);

        var wake = new InMemoryTaskDispatchWake();
        var service = new TaskService(
            new InMemoryTaskRepository(), implants, engagements, TimeProvider.System, bus: null,
            capabilities: new ClassTableCapabilityResolver(), wake);

        return new Harness(engagement.Id, parent, implants, service, wake);
    }
}
