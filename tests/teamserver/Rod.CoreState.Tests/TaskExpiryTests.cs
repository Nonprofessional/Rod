using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// The lost-response expiry (architecture.md Sec 10.3): a dispatched task
/// whose answer was lost in transit closes as failed past the window --
/// nothing re-delivers it and the operator's cancel only reaches Queued,
/// so without the sweep it hangs forever. Channel tasks are never expired
/// (a live channel legitimately runs for hours), and a real result that
/// lands before the sweep wins the race.
/// </summary>
public class TaskExpiryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task ALostTask_ExpiresFailed_PastTheWindow()
    {
        var h = new Harness();
        var issued = await h.Service.IssueAsync(h.TaskFor("shell.exec"));
        Assert.NotNull(await h.Service.DispatchNextAsync(h.Implant.Id));

        // Inside the window nothing expires; past it the task closes failed
        // with the loss named.
        h.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Empty(await h.Service.ExpireLostTasksAsync(TimeSpan.FromHours(24)));

        h.Clock.Advance(TimeSpan.FromHours(2));
        var expired = await h.Service.ExpireLostTasksAsync(TimeSpan.FromHours(24));
        var expiredTask = Assert.Single(expired);
        Assert.Equal(issued.TaskId, expiredTask.TaskId);
        Assert.Equal(TaskOutcome.Failed, expiredTask.Outcome);
        Assert.Contains("no response", expiredTask.Output);

        var stored = await h.Repository.FindAsync(issued.TaskId);
        Assert.Equal(Rod.CoreState.Tasks.TaskStatus.Completed, stored!.Status);
    }

    [Fact]
    public async Task AChannelTask_IsNeverExpired()
    {
        var h = new Harness();
        await h.Service.IssueAsync(h.TaskFor("shell.interact"));
        Assert.NotNull(await h.Service.DispatchNextAsync(h.Implant.Id));

        h.Clock.Advance(TimeSpan.FromDays(30));
        Assert.Empty(await h.Service.ExpireLostTasksAsync(TimeSpan.FromHours(24)));

        var stored = (await h.Repository.ListByImplantAsync(h.Implant.Id)).Single();
        Assert.Equal(Rod.CoreState.Tasks.TaskStatus.Dispatched, stored.Status);
    }

    [Fact]
    public async Task ARealResult_WinningTheRace_SparesTheTask()
    {
        var h = new Harness();
        var issued = await h.Service.IssueAsync(h.TaskFor("shell.exec"));
        Assert.NotNull(await h.Service.DispatchNextAsync(h.Implant.Id));

        // The implant's answer lands just inside the window: the sweep that
        // follows finds the task terminal and reports nothing expired.
        h.Clock.Advance(TimeSpan.FromHours(25));
        var completed = await h.Service.RecordResultAsync(issued.TaskId, "uid=0(root)", TaskOutcome.Succeeded);
        Assert.Equal(TaskOutcome.Succeeded, completed.Outcome);

        Assert.Empty(await h.Service.ExpireLostTasksAsync(TimeSpan.FromHours(24)));
    }

    private sealed class Harness
    {
        public readonly MutableClock Clock = new();
        public readonly InMemoryTaskRepository Repository = new();

        public Harness()
        {
            var engagements = new InMemoryEngagementRepository();
            var engagement = Engagement.Create(EngagementId.New(), "expiry-test", OperatorId.New(), Start);
            engagements.SaveAsync(engagement).GetAwaiter().GetResult();

            Implants = new InMemoryImplantRepository();
            Implant = Implant.Enroll(ImplantId.New(), engagement.Id, Start.AddDays(30), ImplantClass.Implant, Start);
            Implants.SaveAsync(Implant).GetAwaiter().GetResult();

            Service = new TaskService(
                Repository,
                Implants,
                engagements,
                Clock,
                bus: null,
                capabilities: new ClassTableCapabilityResolver(),
                wake: null);
        }

        public InMemoryImplantRepository Implants { get; }
        public Implant Implant { get; }
        public TaskService Service { get; }

        public IssueTaskCommand TaskFor(string verb)
            => new(Implant.EngagementId, Implant.Id, OperatorId.New(), verb, "arg");
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = Start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }
}
