using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
// The domain entity shares its name with System.Threading.Tasks.Task; pin the
// BCL type and reach the entity by its full name where it is constructed.
using Task = System.Threading.Tasks.Task;
// Both namespaces define TaskStatus; the one under test is the entity's.
using TaskStatus = Rod.CoreState.Tasks.TaskStatus;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of the streaming task shape's transcript (architecture.md Sec 10.3):
/// a channel task's output accumulates on the task while it is Dispatched, the
/// final result appends rather than replaces it, and a transcript that outgrows
/// the cap is truncated once instead of pinning memory.
/// </summary>
public class TaskTranscriptTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Rod.CoreState.Tasks.Task ChannelTask()
    {
        var task = Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), EngagementId.New(), ImplantId.New(), OperatorId.New(),
            ChannelVerbs.ShellInteract, arguments: string.Empty, Now);
        task.MarkDispatched(Now);
        return task;
    }

    [Fact]
    public void AppendOutput_AccumulatesChunksOntoTheTranscript()
    {
        var task = ChannelTask();

        task.AppendOutput("$ ");
        task.AppendOutput("whoami\n");

        Assert.Equal("$ whoami\n", task.Output);
        Assert.Equal(TaskStatus.Dispatched, task.Status);
    }

    [Fact]
    public void AppendOutput_IsIllegalOutsideDispatched()
    {
        var queued = Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), EngagementId.New(), ImplantId.New(), OperatorId.New(),
            ChannelVerbs.ShellInteract, arguments: string.Empty, Now);
        Assert.Throws<InvalidOperationException>(() => queued.AppendOutput("x"));

        var completed = ChannelTask();
        completed.Complete("done", TaskOutcome.Succeeded, Now);
        Assert.Throws<InvalidOperationException>(() => completed.AppendOutput("x"));
    }

    [Fact]
    public void Complete_AppendsToTheTranscript_NotReplacesIt()
    {
        var task = ChannelTask();
        task.AppendOutput("hello\n");

        task.Complete("shell exited", TaskOutcome.Succeeded, Now);

        Assert.Equal("hello\nshell exited", task.Output);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
    }

    [Fact]
    public void Complete_OnAOneShotTask_StillSetsTheOutputWhole()
    {
        var task = Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), EngagementId.New(), ImplantId.New(), OperatorId.New(),
            "shell.exec", arguments: "id", Now);
        task.MarkDispatched(Now);

        task.Complete("uid=0", TaskOutcome.Succeeded, Now);

        Assert.Equal("uid=0", task.Output);
    }

    [Fact]
    public void AppendOutput_TruncatesOnceAtTheCap_AndDropsTheRest()
    {
        var task = ChannelTask();

        task.AppendOutput(new string('a', Rod.CoreState.Tasks.Task.MaxTranscriptChars - 10));
        task.AppendOutput(new string('b', 1000));

        Assert.Equal(Rod.CoreState.Tasks.Task.MaxTranscriptChars, task.Output!.Length);
        Assert.EndsWith("...[transcript truncated]", task.Output);
        Assert.StartsWith(new string('a', 10), task.Output);

        // Past the cap every further chunk is dropped, transcript intact.
        task.AppendOutput(new string('c', 1000));
        Assert.Equal(Rod.CoreState.Tasks.Task.MaxTranscriptChars, task.Output.Length);

        // And the final result appends nothing more -- the record is the
        // truncated transcript.
        task.Complete("shell exited", TaskOutcome.Succeeded, Now);
        Assert.DoesNotContain("shell exited", task.Output);
    }

    /// <summary>
    /// Drives <see cref="TaskService.AppendChannelOutputAsync"/> against the
    /// in-memory repositories: the chunk lands on the dispatched task, the
    /// returned view carries the attribution the live fan-out needs, and a
    /// chunk for a completed task is the straggler the transport ignores.
    /// </summary>
    public class TaskServiceAppendTests
    {
        [Fact]
        public async Task AppendChannelOutput_AppendsAndReturnsAttribution()
        {
            var implants = new InMemoryImplantRepository();
            var engagement = EngagementId.New();
            var implant = Implant.Enroll(ImplantId.New(), engagement, DateTimeOffset.UnixEpoch.AddDays(30),
                ImplantClass.Stage2, DateTimeOffset.UnixEpoch);
            await implants.SaveAsync(implant);
            var tasks = new InMemoryTaskRepository();
            var service = new TaskService(tasks, implants, new InMemoryEngagementRepository(), TimeProvider.System);

            var issued = await service.IssueAsync(new IssueTaskCommand(
                engagement, implant.Id, OperatorId.New(),
                ChannelVerbs.ShellInteract, string.Empty));
            var dispatched = await service.DispatchNextAsync(implant.Id);
            Assert.NotNull(dispatched);

            var appended = await service.AppendChannelOutputAsync(issued.TaskId, "$ ");

            Assert.Equal(issued.TaskId, appended.TaskId);
            Assert.Equal(implant.Id, appended.ImplantId);
            Assert.Equal(engagement, appended.EngagementId);
            Assert.Equal(ChannelVerbs.ShellInteract, appended.Verb);

            var stored = await tasks.FindAsync(issued.TaskId);
            Assert.Equal("$ ", stored!.Output);
        }

        [Fact]
        public async Task AppendChannelOutput_OnACompletedTask_ThrowsForTheTransportToIgnore()
        {
            var implants = new InMemoryImplantRepository();
            var engagement = EngagementId.New();
            var implant = Implant.Enroll(ImplantId.New(), engagement, DateTimeOffset.UnixEpoch.AddDays(30),
                ImplantClass.Stage2, DateTimeOffset.UnixEpoch);
            await implants.SaveAsync(implant);
            var service = new TaskService(
                new InMemoryTaskRepository(), implants, new InMemoryEngagementRepository(), TimeProvider.System);

            var issued = await service.IssueAsync(new IssueTaskCommand(
                engagement, implant.Id, OperatorId.New(),
                ChannelVerbs.ShellInteract, string.Empty));
            await service.DispatchNextAsync(implant.Id);
            await service.RecordResultAsync(issued.TaskId, "shell exited", TaskOutcome.Succeeded);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.AppendChannelOutputAsync(issued.TaskId, "late"));
        }
    }
}

/// <summary>
/// Checks of the channel-verb predicate the DNS bridge and the operator input
/// route share: the streaming shape's verbs classify as channels, everything
/// else (including shell.exec, the one-shot shape) does not.
/// </summary>
public class ChannelVerbsTests
{
    [Theory]
    [InlineData("shell.interact")]
    [InlineData("SHELL.INTERACT")]
    public void IsChannelVerb_AdmitsTheStreamingVerbs_CaseInsensitively(string verb)
        => Assert.True(ChannelVerbs.IsChannelVerb(verb));

    [Theory]
    [InlineData("shell.exec")]
    [InlineData("file.push")]
    [InlineData("evasion.sleep")]
    [InlineData("")]
    [InlineData(null)]
    public void IsChannelVerb_DeniesEverythingElse(string? verb)
        => Assert.False(ChannelVerbs.IsChannelVerb(verb));
}
