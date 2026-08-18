using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// The per-implant dispatch wake (architecture.md Sec 10.3): the beacon
/// writer parks on the wake and TaskService releases it on every accepted
/// enqueue, so tasking is pushed downstream rather than polled for. These
/// checks pin the primitive's own contract -- a parked waiter is woken by a
/// release, a release that lands before the wait is not lost, and wakes never
/// cross implants -- and that both enqueue paths through
/// <see cref="TaskService"/> (issuance and requeue) release it.
/// </summary>
public class TaskDispatchWakeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task Waiter_Parks_Until_Released()
    {
        var wake = new InMemoryTaskDispatchWake();
        var implant = ImplantId.New();

        var waiting = wake.WaitAsync(implant);
        Assert.False(waiting.IsCompleted);

        wake.Release(implant);
        await waiting;
    }

    [Fact]
    public async Task Release_BeforeWait_IsNotLost()
    {
        var wake = new InMemoryTaskDispatchWake();
        var implant = ImplantId.New();

        // The writer's claim-then-park shape means a release can land while
        // the writer is still dispatching; the permit must survive until the
        // next wait, or the task it announced would sit queued with nobody
        // awake to push it.
        wake.Release(implant);
        wake.Release(implant);

        await wake.WaitAsync(implant);
        await wake.WaitAsync(implant);
    }

    [Fact]
    public async Task Wakes_DoNotCrossImplants()
    {
        var wake = new InMemoryTaskDispatchWake();
        var a = ImplantId.New();
        var b = ImplantId.New();

        var waiting = wake.WaitAsync(a);
        wake.Release(b);

        Assert.False(waiting.IsCompleted);

        wake.Release(a);
        await waiting;
    }

    [Fact]
    public async Task IssuedTask_ReleasesTheWake()
    {
        var h = await HarnessAsync();
        var waiting = h.Wake.WaitAsync(h.Implant.Id);
        Assert.False(waiting.IsCompleted);

        await h.Service.IssueAsync(TaskFor(h, "shell.exec"));

        await waiting;
    }

    [Fact]
    public async Task IssuedTask_RunsTheIssuedRecord_BeforeTheWakeReleases()
    {
        var h = await HarnessAsync();
        var waiting = h.Wake.WaitAsync(h.Implant.Id);
        Assert.False(waiting.IsCompleted);

        var recordRanBeforeTheRelease = false;
        await h.Service.IssueAsync(
            TaskFor(h, "shell.exec"),
            onIssued: (_, _) =>
            {
                // The issuance's durable record composes before the release:
                // the push dispatch the wake starts audits TaskDispatched on
                // the stream thread, and it must not beat the TaskIssued it
                // follows into the trail (architecture.md Sec 11).
                recordRanBeforeTheRelease = !waiting.IsCompleted;
                return Task.CompletedTask;
            });

        Assert.True(recordRanBeforeTheRelease);
        await waiting;
    }

    [Fact]
    public async Task RequeuedDispatch_ReleasesTheWake()
    {
        var h = await HarnessAsync();
        var issued = await h.Service.IssueAsync(TaskFor(h, "shell.exec"));
        var claimed = await h.Service.DispatchNextAsync(h.Implant.Id);
        Assert.NotNull(claimed);

        // The issuance's permit is still held -- claiming a task does not
        // consume one (the wake is a hint, not a ledger) -- so drain it before
        // parking, or the waiter would complete on the stale permit.
        await h.Wake.WaitAsync(h.Implant.Id);

        var waiting = h.Wake.WaitAsync(h.Implant.Id);
        Assert.False(waiting.IsCompleted);

        await h.Service.RequeueAsync(issued.TaskId);

        await waiting;
    }

    private static async Task<WakeHarness> HarnessAsync()
    {
        var engagements = new InMemoryEngagementRepository();
        var engagement = Engagement.Create(EngagementId.New(), "wake-test", OperatorId.New(), Now);
        await engagements.SaveAsync(engagement);

        var implants = new InMemoryImplantRepository();
        var implant = Implant.Enroll(
            ImplantId.New(), engagement.Id, Now.AddDays(30), ImplantClass.Stage2, Now);
        await implants.SaveAsync(implant);

        var wake = new InMemoryTaskDispatchWake();
        var service = new TaskService(
            new InMemoryTaskRepository(),
            implants,
            engagements,
            TimeProvider.System,
            bus: null,
            capabilities: new ClassTableCapabilityResolver(),
            wake);
        return new WakeHarness(engagement, implant, wake, service);
    }

    private sealed record WakeHarness(
        Engagement Engagement,
        Implant Implant,
        InMemoryTaskDispatchWake Wake,
        TaskService Service);

    private static IssueTaskCommand TaskFor(WakeHarness h, string verb)
        => new(h.Engagement.Id, h.Implant.Id, OperatorId.New(), verb, "arg");
}
