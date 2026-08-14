using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
// The domain entity shares its name with System.Threading.Tasks.Task and this
// file is mostly async, so pin Task to the BCL type and reach the entity by its
// full name where it is constructed.
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// Round-trip checks of the task queue and history -- the core-state layer
/// lift. <see cref="InMemoryTaskRepository"/> drains queued tasks per implant in
/// FIFO enqueue order and exposes both implant- and engagement-scoped history
/// (architecture.md Sec 4.1, Sec 10.3). Engagement scoping keeps a caller in one
/// engagement from seeing another's tasks (architecture.md Sec 3).
/// </summary>
public class TaskQueueHistoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Rod.CoreState.Tasks.Task Issue(
        EngagementId engagement,
        ImplantId implant,
        string verb,
        DateTimeOffset at)
        => Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), engagement, implant, OperatorId.New(), verb, arguments: string.Empty, at);

    [Fact]
    public async Task NextPending_DrainsQueuedTasks_InFifoOrder()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var implant = ImplantId.New();

        // Equal CreatedAt timestamps must not make dequeue ambiguous; the queue
        // tracks enqueue order explicitly.
        var t1 = Issue(engagement, implant, "shell.exec", Now);
        var t2 = Issue(engagement, implant, "file.push", Now);
        var t3 = Issue(engagement, implant, "recon.portscan", Now);
        await tasks.SaveAsync(t1);
        await tasks.SaveAsync(t2);
        await tasks.SaveAsync(t3);

        // NextPendingAsync peeks the oldest Queued task; the caller (TaskService)
        // is what advances the drain by marking it Dispatched and re-saving.
        // Walk that loop here so each peek yields the next task in enqueue order.
        var first = await tasks.NextPendingAsync(implant);
        first!.MarkDispatched(Now);
        await tasks.SaveAsync(first);
        var second = await tasks.NextPendingAsync(implant);
        second!.MarkDispatched(Now);
        await tasks.SaveAsync(second);
        var third = await tasks.NextPendingAsync(implant);
        third!.MarkDispatched(Now);
        await tasks.SaveAsync(third);

        Assert.Equal(new[] { t1.Id, t2.Id, t3.Id }, new[] { first.Id, second.Id, third.Id });

        // Once drained, no further pending task.
        Assert.Null(await tasks.NextPendingAsync(implant));
    }

    [Fact]
    public async Task ListByImplant_ReturnsHistory_OldestFirst()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var implant = ImplantId.New();

        var t1 = Issue(engagement, implant, "shell.exec", Now);
        var t2 = Issue(engagement, implant, "file.push", Now.AddSeconds(1));
        await tasks.SaveAsync(t1);
        await tasks.SaveAsync(t2);

        var history = await tasks.ListByImplantAsync(implant);

        Assert.Equal(new[] { t1.Id, t2.Id }, history.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task ListByEngagement_ReturnsHistory_AcrossImplants_OldestFirst()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var implantA = ImplantId.New();
        var implantB = ImplantId.New();

        var tA = Issue(engagement, implantA, "shell.exec", Now);
        var tB = Issue(engagement, implantB, "file.push", Now);
        await tasks.SaveAsync(tA);
        await tasks.SaveAsync(tB);

        var history = await tasks.ListByEngagementAsync(engagement);

        Assert.Equal(new[] { tA.Id, tB.Id }, history.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task History_StaysScopedByEngagement()
    {
        var tasks = new InMemoryTaskRepository();
        var engagementA = EngagementId.New();
        var engagementB = EngagementId.New();
        var implantA = ImplantId.New();
        var implantB = ImplantId.New();

        await tasks.SaveAsync(Issue(engagementA, implantA, "shell.exec", Now));
        await tasks.SaveAsync(Issue(engagementB, implantB, "shell.exec", Now));

        var aHistory = await tasks.ListByEngagementAsync(engagementA);

        var only = Assert.Single(aHistory);
        Assert.Equal(engagementA, only.EngagementId);
    }

    [Fact]
    public async Task NextPending_StaysScopedByImplant()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var implantA = ImplantId.New();
        var implantB = ImplantId.New();

        await tasks.SaveAsync(Issue(engagement, implantA, "shell.exec", Now));

        // A queued task for implant A is not visible as pending for implant B.
        Assert.Null(await tasks.NextPendingAsync(implantB));
        Assert.NotNull(await tasks.NextPendingAsync(implantA));
    }

    [Fact]
    public async Task ClaimNextPending_MarksDispatched_AndAdvancesTheQueue()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var implant = ImplantId.New();

        var t1 = Issue(engagement, implant, "shell.exec", Now);
        var t2 = Issue(engagement, implant, "file.push", Now);
        await tasks.SaveAsync(t1);
        await tasks.SaveAsync(t2);

        // The claim returns the oldest queued task already Dispatched at the
        // given time and persisted; the next claim moves on to the next task.
        var first = await tasks.ClaimNextPendingAsync(implant, Now.AddMinutes(1));
        var second = await tasks.ClaimNextPendingAsync(implant, Now.AddMinutes(2));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(t1.Id, first!.Id);
        Assert.Equal(t2.Id, second!.Id);
        Assert.Equal(Rod.CoreState.Tasks.TaskStatus.Dispatched, first.Status);
        Assert.Equal(Now.AddMinutes(1), first.DispatchedAt);
        Assert.Null(await tasks.ClaimNextPendingAsync(implant, Now.AddMinutes(3)));

        // The claimed state is what a later peek observes, not a transient mark.
        Assert.Equal(Rod.CoreState.Tasks.TaskStatus.Dispatched, (await tasks.FindAsync(t1.Id))!.Status);
    }

    [Fact]
    public async Task ConcurrentClaims_NeverHandOutTheSameTask()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var implant = ImplantId.New();

        const int count = 20;
        for (var i = 0; i < count; i++)
            await tasks.SaveAsync(Issue(engagement, implant, "shell.exec", Now));

        // Two overlapping beacon sessions race for the same queue; the claim is
        // atomic, so every handed-out task is distinct and Dispatched exactly
        // once.
        var claimed = await Task.WhenAll(
            Enumerable.Range(0, count)
                .Select(i => tasks.ClaimNextPendingAsync(implant, Now.AddSeconds(i))));

        Assert.Equal(count, claimed.Select(t => t!.Id).Distinct().Count());
        Assert.All(claimed, t => Assert.Equal(Rod.CoreState.Tasks.TaskStatus.Dispatched, t!.Status));
        Assert.Null(await tasks.ClaimNextPendingAsync(implant, Now.AddHours(1)));
    }

    [Fact]
    public async Task Requeue_ReturnsDispatchedTaskToTheQueue_ClearingItsStamp()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var implant = ImplantId.New();

        var t1 = Issue(engagement, implant, "shell.exec", Now);
        await tasks.SaveAsync(t1);

        var claimed = (await tasks.ClaimNextPendingAsync(implant, Now.AddMinutes(1)))!;

        // The downstream write failed: the task returns to the queue and the
        // next claim hands it out again, fresh.
        claimed.Requeue();
        await tasks.SaveAsync(claimed);

        Assert.Equal(Rod.CoreState.Tasks.TaskStatus.Queued, claimed.Status);
        Assert.Null(claimed.DispatchedAt);

        var reclaimed = await tasks.ClaimNextPendingAsync(implant, Now.AddMinutes(2));
        Assert.NotNull(reclaimed);
        Assert.Equal(t1.Id, reclaimed!.Id);
        Assert.Equal(Now.AddMinutes(2), reclaimed.DispatchedAt);
    }
}
