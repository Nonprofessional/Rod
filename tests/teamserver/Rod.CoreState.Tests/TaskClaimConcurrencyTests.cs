using System.Collections.Concurrent;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
// Pin Task to the BCL type (the TaskQueueHistoryTests convention): this file is
// async-heavy, so the domain entity is reached by its full name.
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// Multi-threaded hammer tests for <see cref="InMemoryTaskRepository"/> claim
/// atomicity (architecture.md Sec 10.3). ClaimNextPendingAsync guards the
/// peek-and-mark transition with a lock; these tests drive real threads into it
/// and assert every task is handed out exactly once. (The older
/// TaskQueueHistoryTests concurrent-claims test awaits Task.FromResult-backed
/// calls, which complete synchronously, so it never actually contends.)
/// </summary>
public class TaskClaimConcurrencyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Rod.CoreState.Tasks.Task Issue(
        EngagementId engagement,
        ImplantId implant,
        DateTimeOffset at)
        => Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), engagement, implant, OperatorId.New(), "shell.exec", arguments: string.Empty, at);

    [Fact]
    public async Task ConcurrentClaims_OneImplant_HandOutEachTaskExactlyOnce()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var implant = ImplantId.New();

        const int taskCount = 400;
        const int workerCount = 16;
        for (var i = 0; i < taskCount; i++)
            await tasks.SaveAsync(Issue(engagement, implant, Now));

        var claimed = new ConcurrentBag<TaskId>();
        var gate = new ManualResetEventSlim(initialState: false);

        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            while (true)
            {
                var next = await tasks.ClaimNextPendingAsync(implant, Now);
                if (next is null)
                    return;
                claimed.Add(next.Id);
            }
        })).ToArray();

        gate.Set();
        await Task.WhenAll(workers);

        // Every queued task was handed out and none twice: the lock makes the
        // peek-and-mark transition atomic, so two racing workers never observe
        // the same Queued task.
        Assert.Equal(taskCount, claimed.Count);
        Assert.Equal(taskCount, claimed.Distinct().Count());

        // And the claimed state is what the store still observes afterwards.
        Assert.Null(await tasks.NextPendingAsync(implant));
        foreach (var id in claimed)
            Assert.Equal(Rod.CoreState.Tasks.TaskStatus.Dispatched, (await tasks.FindAsync(id))!.Status);
    }

    [Fact]
    public async Task ConcurrentClaims_AcrossImplants_StayScopedPerImplant()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();

        const int implantCount = 4;
        const int perImplant = 100;
        const int workersPerImplant = 4;

        var implants = Enumerable.Range(0, implantCount).Select(_ => ImplantId.New()).ToArray();
        foreach (var implant in implants)
            for (var i = 0; i < perImplant; i++)
                await tasks.SaveAsync(Issue(engagement, implant, Now));

        // One gate so every worker of every implant enters together; the single
        // claim lock serializes across implants as well as within one.
        var claimed = new ConcurrentDictionary<ImplantId, ConcurrentBag<TaskId>>();
        var gate = new ManualResetEventSlim(initialState: false);

        var workers = implants.SelectMany(implant =>
            Enumerable.Range(0, workersPerImplant).Select(_ => Task.Run(async () =>
            {
                var bag = claimed.GetOrAdd(implant, _ => new ConcurrentBag<TaskId>());
                gate.Wait();
                while (true)
                {
                    var next = await tasks.ClaimNextPendingAsync(implant, Now);
                    if (next is null)
                        return;
                    bag.Add(next.Id);
                }
            }))).ToArray();

        gate.Set();
        await Task.WhenAll(workers);

        foreach (var implant in implants)
        {
            var ids = claimed[implant];
            Assert.Equal(perImplant, ids.Count);
            Assert.Equal(perImplant, ids.Distinct().Count());
            Assert.Null(await tasks.NextPendingAsync(implant));
        }
    }
}
