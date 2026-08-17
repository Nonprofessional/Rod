using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of the rules-of-engagement gate (architecture.md Sec 9 -- ROE
/// guardrails). The engagement's ROE profile is the operator-set scope of what
/// its operators may task, enforced at issuance after the class gate: a task
/// outside the profile is refused before it is queued, with a message naming
/// the violated rule. Drives <see cref="TaskService"/> against the in-memory
/// repositories, with real engagement records so the gate reads their scope.
/// </summary>
public class RoeGateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static async Task<RoeHarness> EngageAsync(RoeProfile? roe = null)
    {
        var engagements = new InMemoryEngagementRepository();
        var engagement = Engagement.Create(EngagementId.New(), "roe-test", OperatorId.New(), Now);
        if (roe is not null)
            engagement.ApplyRoe(roe);
        await engagements.SaveAsync(engagement);

        var implants = new InMemoryImplantRepository();
        var implant = Implant.Enroll(
            ImplantId.New(), engagement.Id, "key-roe", Now.AddDays(30), ImplantClass.Stage2, Now);
        await implants.SaveAsync(implant);

        var service = new TaskService(new InMemoryTaskRepository(), implants, engagements, TimeProvider.System);
        return new RoeHarness(engagements, engagement, implant, service);
    }

    private sealed record RoeHarness(
        InMemoryEngagementRepository Engagements,
        Engagement Engagement,
        Implant Implant,
        TaskService Service);

    private static IssueTaskCommand TaskFor(RoeHarness h, string verb)
        => new(h.Engagement.Id, h.Implant.Id, OperatorId.New(), verb, "arg");

    [Fact]
    public async Task UnrestrictedScope_IssuesNormally()
    {
        var h = await EngageAsync();

        var issued = await h.Service.IssueAsync(TaskFor(h, "shell.exec"));

        Assert.Equal("shell.exec", issued.Verb);
    }

    [Fact]
    public async Task VerbOutsidePermittedVerbs_IsRefusedNamingTheRule()
    {
        var h = await EngageAsync(new RoeProfile(["shell.exec", "recon.*"], null));

        // file.pull is class-admissible for Stage-2, so the refusal is the ROE
        // gate's, not the class gate's.
        var ex = await Assert.ThrowsAsync<TaskRejectedException>(
            () => h.Service.IssueAsync(TaskFor(h, "file.pull")));

        Assert.Equal(TaskRejectionReason.RoeViolation, ex.Reason);
        Assert.Contains("permitted verbs", ex.Message);
        Assert.Contains("file.pull", ex.Message);
    }

    [Theory]
    [InlineData("shell.exec")]
    [InlineData("recon.portscan")]
    [InlineData("recon.hostenum")]
    public async Task WildcardAndExactPatterns_AdmitTheirVerbs(string verb)
    {
        var h = await EngageAsync(new RoeProfile(["shell.exec", "recon.*"], null));

        var issued = await h.Service.IssueAsync(TaskFor(h, verb));

        Assert.Equal(verb, issued.Verb);
    }

    [Fact]
    public async Task ImplantOutsidePermittedTargets_IsRefusedNamingTheRule()
    {
        var h = await EngageAsync(new RoeProfile(null, ["ffffffffffffffffffffffffffffffff"]));

        var ex = await Assert.ThrowsAsync<TaskRejectedException>(
            () => h.Service.IssueAsync(TaskFor(h, "shell.exec")));

        Assert.Equal(TaskRejectionReason.RoeViolation, ex.Reason);
        Assert.Contains("permitted targets", ex.Message);
        Assert.Contains(h.Implant.Id.ToString(), ex.Message);
    }

    [Fact]
    public async Task ImplantInsidePermittedTargets_IsIssued()
    {
        var h = await EngageAsync();
        h.Engagement.ApplyRoe(new RoeProfile(null, [h.Implant.Id.ToString()]));
        await h.Engagements.SaveAsync(h.Engagement);

        var issued = await h.Service.IssueAsync(TaskFor(h, "shell.exec"));

        Assert.Equal("shell.exec", issued.Verb);
    }

    [Fact]
    public async Task ApplyingAnEmptyProfile_ReopensTheEngagement()
    {
        var h = await EngageAsync(new RoeProfile(["recon.*"], null));
        h.Engagement.ApplyRoe(RoeProfile.Unrestricted);
        await h.Engagements.SaveAsync(h.Engagement);

        var issued = await h.Service.IssueAsync(TaskFor(h, "shell.exec"));

        Assert.Equal("shell.exec", issued.Verb);
    }

    [Fact]
    public void Profile_NormalizesBlankAndDuplicateEntries()
    {
        var profile = new RoeProfile(
            ["shell.exec", " shell.exec ", "", "  "],
            [" a ", "a"]);

        Assert.Equal(["shell.exec"], profile.PermittedVerbs);
        Assert.Equal(["a"], profile.PermittedImplants);
    }
}
