using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
// The domain entity and its status enum shadow their BCL names once Task is
// pinned to System.Threading.Tasks.Task, so pin the status to the domain one
// too.
using Task = System.Threading.Tasks.Task;
using TaskStatus = Rod.CoreState.Tasks.TaskStatus;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of the queued-task retraction (architecture.md Sec 10.3): an operator
/// can take a task back before the implant wakes, and the retraction is
/// claim-proof -- a cancelled task is never handed to a dispatch, and a task
/// already handed to a stream is no longer cancellable. Covers the entity
/// transition, the repository's atomic cancel (against the in-memory adapter),
/// and the service's scoping and fan-out.
/// </summary>
public class TaskCancellationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Rod.CoreState.Tasks.Task Issue(
        EngagementId engagement,
        ImplantId implant,
        string verb = "shell.exec")
        => Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), engagement, implant, OperatorId.New(), verb, arguments: string.Empty, Now);

    [Fact]
    public void Cancel_FromQueued_IsTerminal()
    {
        var task = Issue(EngagementId.New(), ImplantId.New());

        task.Cancel(Now);

        Assert.Equal(TaskStatus.Cancelled, task.Status);
        Assert.Equal(Now, task.CancelledAt);
        // Terminal: a second cancel, a dispatch, or a completion are all
        // refused out of the Cancelled state.
        Assert.Throws<InvalidOperationException>(() => task.Cancel(Now));
        Assert.Throws<InvalidOperationException>(() => task.MarkDispatched(Now));
        Assert.Throws<InvalidOperationException>(() => task.Complete("late", TaskOutcome.Succeeded, Now));
    }

    [Fact]
    public void Cancel_IsRefused_FromDispatchedOrCompleted()
    {
        var dispatched = Issue(EngagementId.New(), ImplantId.New());
        dispatched.MarkDispatched(Now);
        Assert.Throws<InvalidOperationException>(() => dispatched.Cancel(Now));

        var completed = Issue(EngagementId.New(), ImplantId.New());
        completed.MarkDispatched(Now);
        completed.Complete("done", TaskOutcome.Succeeded, Now);
        Assert.Throws<InvalidOperationException>(() => completed.Cancel(Now));
    }

    [Fact]
    public async Task RepositoryCancel_MarksItCancelled_AndClaimsSkipIt()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var implant = ImplantId.New();
        var t1 = Issue(engagement, implant);
        var t2 = Issue(engagement, implant);
        await tasks.SaveAsync(t1);
        await tasks.SaveAsync(t2);

        var cancelled = await tasks.CancelAsync(t1.Id, Now);

        Assert.NotNull(cancelled);
        Assert.Equal(TaskStatus.Cancelled, cancelled!.Status);
        Assert.Equal(t1.Id, cancelled.Id);

        // The dispatch claim skips the cancelled task and hands out the next
        // queued one -- the retraction is claim-proof, not just hidden.
        var claimed = await tasks.ClaimNextPendingAsync(implant, Now);
        Assert.Equal(t2.Id, claimed!.Id);
        Assert.Null(await tasks.NextPendingAsync(implant));
    }

    [Fact]
    public async Task RepositoryCancel_OfADispatchedTask_ReturnsItUnchanged()
    {
        var tasks = new InMemoryTaskRepository();
        var task = Issue(EngagementId.New(), ImplantId.New());
        await tasks.SaveAsync(task);
        var claimed = await tasks.ClaimNextPendingAsync(task.ImplantId, Now);
        Assert.NotNull(claimed);

        var after = await tasks.CancelAsync(task.Id, Now);

        // The claim won the race: the cancel neither throws nor rewrites
        // history -- the task comes back dispatched for the caller to refuse.
        Assert.Equal(TaskStatus.Dispatched, after!.Status);
        Assert.Null(after.CancelledAt);
    }

    [Fact]
    public async Task RepositoryCancel_OfAnUnknownTask_ReturnsNull()
    {
        var tasks = new InMemoryTaskRepository();

        Assert.Null(await tasks.CancelAsync(TaskId.New(), Now));
    }

    private sealed class RecordingBus : ILiveEventBus
    {
        public List<LiveEvent> Published { get; } = [];

        public Task PublishAsync(LiveEvent @event, CancellationToken cancellationToken = default)
        {
            Published.Add(@event);
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
    public async Task ServiceCancel_RetractsTheQueuedTask_AndPublishes()
    {
        var tasks = new InMemoryTaskRepository();
        var implants = new InMemoryImplantRepository();
        var bus = new RecordingBus();
        var service = new TaskService(
            tasks, implants, new InMemoryEngagementRepository(), TimeProvider.System, bus);
        var engagement = EngagementId.New();
        var implant = Implant.Enroll(ImplantId.New(), engagement, Now.AddDays(30), ImplantClass.Stage2, Now);
        await implants.SaveAsync(implant);

        var issued = await service.IssueAsync(
            new IssueTaskCommand(engagement, implant.Id, OperatorId.New(), "shell.exec", "whoami"));
        var cancelledBy = OperatorId.New();

        var cancelled = await service.CancelAsync(engagement, issued.TaskId, cancelledBy);

        Assert.Equal(issued.TaskId, cancelled.TaskId);
        Assert.Equal(cancelledBy, cancelled.CancelledBy);
        Assert.Equal(TaskStatus.Cancelled, (await tasks.FindAsync(issued.TaskId))!.Status);

        // The retraction fans out live, attributed to the cancelling operator.
        var @event = Assert.Single(bus.Published, e => e.Kind == LiveEventKind.TaskCancelled);
        Assert.Equal(engagement, @event.EngagementId);
        Assert.Equal(cancelledBy, @event.OperatorId);
        Assert.Equal(issued.TaskId, @event.TaskId);
    }

    [Fact]
    public async Task ServiceCancel_RefusesADispatchedTask()
    {
        var tasks = new InMemoryTaskRepository();
        var implants = new InMemoryImplantRepository();
        var service = new TaskService(
            tasks, implants, new InMemoryEngagementRepository(), TimeProvider.System);
        var engagement = EngagementId.New();
        var implant = Implant.Enroll(ImplantId.New(), engagement, Now.AddDays(30), ImplantClass.Stage2, Now);
        await implants.SaveAsync(implant);

        var issued = await service.IssueAsync(
            new IssueTaskCommand(engagement, implant.Id, OperatorId.New(), "shell.exec", "whoami"));
        var dispatched = await service.DispatchNextAsync(implant.Id);
        Assert.Equal(issued.TaskId, dispatched!.TaskId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CancelAsync(engagement, issued.TaskId, OperatorId.New()));
        Assert.Contains("cannot be cancelled", ex.Message);
    }

    [Fact]
    public async Task ServiceCancel_RefusesAForeignEngagement()
    {
        var tasks = new InMemoryTaskRepository();
        var implants = new InMemoryImplantRepository();
        var service = new TaskService(
            tasks, implants, new InMemoryEngagementRepository(), TimeProvider.System);
        var engagement = EngagementId.New();
        var implant = Implant.Enroll(ImplantId.New(), engagement, Now.AddDays(30), ImplantClass.Stage2, Now);
        await implants.SaveAsync(implant);

        var issued = await service.IssueAsync(
            new IssueTaskCommand(engagement, implant.Id, OperatorId.New(), "shell.exec", "whoami"));

        // A caller scoped to another engagement cannot even name this task:
        // the engagement binding is checked before anything is retracted
        // (architecture.md Sec 3).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CancelAsync(EngagementId.New(), issued.TaskId, OperatorId.New()));
        Assert.Equal(TaskStatus.Queued, (await tasks.FindAsync(issued.TaskId))!.Status);
    }
}
