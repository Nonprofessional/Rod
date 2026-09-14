using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
// The domain entity shadows its BCL name; pin the async type like the other
// task suites do.
using Task = System.Threading.Tasks.Task;
using TaskStatus = Rod.CoreState.Tasks.TaskStatus;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of the first-result-wins discipline (architecture.md Sec 10.3 --
/// the dispatch strand): the receive-ack arm makes retransmitted results a
/// normal occurrence (an implant resends a cached result after a stream
/// death), so the completion transition is atomic in the repository and a
/// duplicate loses the race instead of double-recording. Covers the
/// repository's atomic complete (against the in-memory adapter) and the
/// service's refusal shape.
/// </summary>
public class TaskResultFirstWinsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Rod.CoreState.Tasks.Task Dispatched(EngagementId engagement, ImplantId implant)
    {
        var task = Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), engagement, implant, OperatorId.New(), "shell.exec", arguments: string.Empty, Now);
        task.MarkDispatched(Now);
        return task;
    }

    [Fact]
    public async Task RepositoryComplete_SecondResultLoses_TheFirstWins()
    {
        var tasks = new InMemoryTaskRepository();
        var task = Dispatched(EngagementId.New(), ImplantId.New());
        await tasks.SaveAsync(task);

        var first = await tasks.CompleteAsync(task.Id, "the first answer", TaskOutcome.Succeeded, Now);
        var second = await tasks.CompleteAsync(task.Id, "a late duplicate", TaskOutcome.Failed, Now);

        Assert.NotNull(first);
        Assert.Null(second);

        var reread = await tasks.FindAsync(task.Id);
        Assert.Equal(TaskStatus.Completed, reread!.Status);
        Assert.Equal("the first answer", reread.Output);
        Assert.Equal(TaskOutcome.Succeeded, reread.Outcome);
    }

    [Fact]
    public async Task RepositoryComplete_ConcurrentResults_ExactlyOneWins()
    {
        var tasks = new InMemoryTaskRepository();
        var task = Dispatched(EngagementId.New(), ImplantId.New());
        await tasks.SaveAsync(task);

        // Two overlapping streams delivering the same retransmitted result:
        // both race the transition, exactly one records it.
        var first = System.Threading.Tasks.Task.Run(
            () => tasks.CompleteAsync(task.Id, "from stream A", TaskOutcome.Succeeded, Now));
        var second = System.Threading.Tasks.Task.Run(
            () => tasks.CompleteAsync(task.Id, "from stream B", TaskOutcome.Succeeded, Now));
        var winners = (await System.Threading.Tasks.Task.WhenAll(first, second)).Count(t => t is not null);

        Assert.Equal(1, winners);
        var reread = await tasks.FindAsync(task.Id);
        Assert.Equal(TaskStatus.Completed, reread!.Status);
    }

    [Fact]
    public async Task RepositoryComplete_IsRefusedForUnknownOrUndispatched()
    {
        var tasks = new InMemoryTaskRepository();
        Assert.Null(await tasks.CompleteAsync(TaskId.New(), "nobody", TaskOutcome.Failed, Now));

        var queued = Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), EngagementId.New(), ImplantId.New(), OperatorId.New(),
            "shell.exec", arguments: string.Empty, Now);
        await tasks.SaveAsync(queued);
        Assert.Null(await tasks.CompleteAsync(queued.Id, "never dispatched", TaskOutcome.Failed, Now));
    }

    [Fact]
    public async Task ServiceRecordResult_RefusesTheDuplicate_WithTheRefusalShape()
    {
        var tasks = new InMemoryTaskRepository();
        var implants = new InMemoryImplantRepository();
        var engagements = new InMemoryEngagementRepository();
        var service = new TaskService(tasks, implants, engagements, FakeClock());

        var task = Dispatched(EngagementId.New(), ImplantId.New());
        await tasks.SaveAsync(task);

        var completed = await service.RecordResultAsync(task.Id, "the first answer", TaskOutcome.Succeeded);
        Assert.Equal("the first answer", completed.Output);

        // The duplicate surfaces as the same InvalidOperationException shape
        // the unknown-task path always had, so the transport's existing
        // ignore-and-continue covers it.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RecordResultAsync(task.Id, "a late duplicate", TaskOutcome.Failed));
    }

    private static TimeProvider FakeClock() => new FakeTime(Now);

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
