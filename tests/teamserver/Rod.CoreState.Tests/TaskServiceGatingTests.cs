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
            ImplantId.New(), engagement, Now.AddDays(30), @class, Now);
        await implants.SaveAsync(implant);
        return implant;
    }

    // No engagement records are stored, so the ROE lookup resolves nothing and
    // the profile in force is the unrestricted default -- these tests exercise
    // the class gate, not the scope gate (RoeGateTests covers that).
    private static TaskService NewService(InMemoryImplantRepository implants)
        => new(new InMemoryTaskRepository(), implants, new InMemoryEngagementRepository(), TimeProvider.System);

    [Theory]
    [InlineData(ImplantClass.Stage2, "shell.exec")]
    [InlineData(ImplantClass.Stage2, "file.pull")]
    [InlineData(ImplantClass.Stager, "file.pull")]
    [InlineData(ImplantClass.WebShell, "shell.exec")]
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
        // Sec 7). The refusal happens before the verb gate, so even a verb
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

    [Fact]
    public async Task IssueAsync_DefaultResolverIsTheStrictClassTable()
    {
        // The default capability resolver (architecture.md Sec 5.2/10.3) is the
        // per-class reduced verb set alone -- it admits no contract-and-dispatch-
        // only verb (evasion, exploit) on its own. That path opens when the
        // tradecraft layer wires a registry-backed resolver in (architecture.md
        // Sec 10.2); core state alone stays strict, so this test host and the
        // core-state unit tests keep the behavior they had before .
        var implants = new InMemoryImplantRepository();
        var engagement = EngagementId.New();
        var implant = await EnrollAsync(implants, engagement, ImplantClass.Stage2);
        var service = NewService(implants);

        var ex = await Assert.ThrowsAsync<TaskRejectedException>(
            () => service.IssueAsync(
                new IssueTaskCommand(engagement, implant.Id, OperatorId.New(), "evasion.avoid", "arg")));

        Assert.Equal(TaskRejectionReason.UnsupportedVerbForClass, ex.Reason);
    }

    [Fact]
    public async Task IssueAsync_AdmitsAVerbTheCapabilityResolverAllows()
    {
        // The task gate consults the capability resolver (architecture.md Sec
        // 10.3): a resolver that admits a verb the class set does not -- standing
        // in for the tradecraft-backed resolver a registered module satisfies --
        // lets the verb through. This is the mechanic in isolation: the
        // resolver, not the class table alone, is the gate authority.
        var implants = new InMemoryImplantRepository();
        var engagement = EngagementId.New();
        var implant = await EnrollAsync(implants, engagement, ImplantClass.Stage2);
        var service = new TaskService(
            new InMemoryTaskRepository(),
            implants,
            new InMemoryEngagementRepository(),
            TimeProvider.System,
            bus: null,
            capabilities: new AllowingResolver("evasion.avoid"),
            wake: null);

        var issued = await service.IssueAsync(
            new IssueTaskCommand(engagement, implant.Id, OperatorId.New(), "evasion.avoid", "arg"));

        Assert.Equal("evasion.avoid", issued.Verb);
    }

    // A resolver that admits exactly one verb regardless of class -- the
    // stand-in for the registry-backed resolver the tradecraft layer supplies
    // when a module is registered for a no-class-gate verb.
    private sealed class AllowingResolver : ITaskCapabilityResolver
    {
        private readonly string _verb;

        public AllowingResolver(string verb) => _verb = verb;

        public bool IsDispatchable(ImplantClass @class, string verb)
            => string.Equals(verb, _verb, StringComparison.OrdinalIgnoreCase);
    }
}
