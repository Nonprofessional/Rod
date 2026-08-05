using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of the task-issuance gate (architecture.md Sec 5.2, Sec 10.3). The
/// implant's class reduced verb set is the authority for what it may run: an
/// allowed verb is queued, an unsupported one is refused before queueing, and
/// the implant's engagement binding is enforced at issue time too. Drives
/// <see cref="TaskService"/> against the in-memory repositories the rest of the
/// core-state tests use.
/// </summary>
public class TaskServiceGatingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static async Task<Implant> EnrollAsync(
        InMemoryImplantRepository implants,
        EngagementId engagement,
        ImplantClass @class)
    {
        var implant = Implant.Enroll(
            ImplantId.New(), engagement, "key-" + @class, Now.AddDays(30), @class, Now);
        await implants.SaveAsync(implant);
        return implant;
    }

    private static TaskService NewService(InMemoryImplantRepository implants)
        => new(new InMemoryTaskRepository(), implants, TimeProvider.System);

    [Theory]
    [InlineData(ImplantClass.Stage2, "shell.exec")]
    [InlineData(ImplantClass.Stage2, "file.pull")]
    [InlineData(ImplantClass.Stager, "file.pull")]
    [InlineData(ImplantClass.WebShell, "shell.exec")]
    [InlineData(ImplantClass.Ephemeral, "probe.read")]
    [InlineData(ImplantClass.Pivot, "tunnel.open")]
    public async Task IssueAsync_AcceptsAVerbInFromClassSet(ImplantClass @class, string verb)
    {
        var implants = new InMemoryImplantRepository();
        var engagement = EngagementId.New();
        var implant = await EnrollAsync(implants, engagement, @class);
        var service = NewService(implants);

        var issued = await service.IssueAsync(
            new IssueTaskCommand(engagement, implant.Id, OperatorId.New(), verb, "arg"));

        Assert.Equal(verb, issued.Verb);
        Assert.Equal(implant.Id, issued.ImplantId);
    }

    [Theory]
    [InlineData(ImplantClass.Stager, "shell.exec")]
    [InlineData(ImplantClass.WebShell, "tunnel.open")]
    [InlineData(ImplantClass.Ephemeral, "file.push")]
    [InlineData(ImplantClass.Pivot, "shell.exec")]
    public async Task IssueAsync_RejectsAVerbOutsideTheClassSet(ImplantClass @class, string verb)
    {
        var implants = new InMemoryImplantRepository();
        var engagement = EngagementId.New();
        var implant = await EnrollAsync(implants, engagement, @class);
        var service = NewService(implants);

        var ex = await Assert.ThrowsAsync<TaskRejectedException>(
            () => service.IssueAsync(
                new IssueTaskCommand(engagement, implant.Id, OperatorId.New(), verb, "arg")));

        Assert.Equal(TaskRejectionReason.UnsupportedVerbForClass, ex.Reason);
    }

    [Fact]
    public async Task IssueAsync_RejectsUnknownImplant()
    {
        var service = NewService(new InMemoryImplantRepository());

        var ex = await Assert.ThrowsAsync<TaskRejectedException>(
            () => service.IssueAsync(
                new IssueTaskCommand(EngagementId.New(), ImplantId.New(), OperatorId.New(), "shell.exec", "arg")));

        Assert.Equal(TaskRejectionReason.UnknownImplant, ex.Reason);
    }

    [Fact]
    public async Task IssueAsync_RejectsImplantFromAnotherEngagement()
    {
        var implants = new InMemoryImplantRepository();
        var implant = await EnrollAsync(implants, EngagementId.New(), ImplantClass.Stage2);
        var service = NewService(implants);

        var ex = await Assert.ThrowsAsync<TaskRejectedException>(
            () => service.IssueAsync(
                new IssueTaskCommand(EngagementId.New(), implant.Id, OperatorId.New(), "shell.exec", "arg")));

        Assert.Equal(TaskRejectionReason.ImplantEngagementMismatch, ex.Reason);
    }

    [Fact]
    public async Task IssueAsync_RejectsRetiredImplant()
    {
        // A retired implant is out of operation and untaskable (architecture.md
        // Sec 7, M4.4). The refusal happens before the verb gate, so even a verb
        // in the implant's class set is refused once the implant is retired.
        var implants = new InMemoryImplantRepository();
        var engagement = EngagementId.New();
        var implant = await EnrollAsync(implants, engagement, ImplantClass.Stage2);
        implant.Retire(Now);
        var service = NewService(implants);

        var ex = await Assert.ThrowsAsync<TaskRejectedException>(
            () => service.IssueAsync(
                new IssueTaskCommand(engagement, implant.Id, OperatorId.New(), "shell.exec", "arg")));

        Assert.Equal(TaskRejectionReason.ImplantRetired, ex.Reason);
    }
}
