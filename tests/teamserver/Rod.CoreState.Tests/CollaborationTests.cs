using Rod.CoreState.Application;
using Rod.CoreState.Collaboration;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Tasks;
// The domain entity shares its name with System.Threading.Tasks.Task; the
// BCL type is the one the async tests return, so it wins the alias.
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// The collaboration domain units (architecture.md Sec 15.3, ADR-005):
/// the task-reference grammar (what extracts, what stays literal), the
/// aggregate bounds, the reference service's validate-and-substitute pair
/// over the in-memory stores, the harvester's once-only landing, and the
/// issuance gate's refusal of a dangling reference.
/// </summary>
public class CollaborationTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void TaskReferences_ExtractWellFormedTokens_InBothGuidForms()
    {
        var task = Guid.NewGuid();
        var cred = Guid.NewGuid();
        var text = $"see @task-{task} and $cred-{cred:N} plus $cred-{cred}";

        var tokens = TaskReferences.Extract(text);

        Assert.Equal(3, tokens.Count);
        Assert.Equal(TaskReferenceKind.Task, tokens[0].Kind);
        Assert.Equal(task, tokens[0].Id);
        Assert.Equal(TaskReferenceKind.Credential, tokens[1].Kind);
        Assert.Equal(cred, tokens[1].Id);
        Assert.Equal(TaskReferenceKind.Credential, tokens[2].Kind);
        Assert.Equal($"@task-{task}", tokens[0].Render());
    }

    [Fact]
    public void TaskReferences_LeaveMalformedMarkersAsLiteralText()
    {
        var tokens = TaskReferences.Extract("email me @task-please, cost is $cred-5, see @task-not-a-guid");

        // None of the markers carry a guid, so none extract: prose stays
        // prose, never a dangling reference the publisher must refuse.
        Assert.Empty(tokens);
        Assert.False(TaskReferences.ContainsCredential("$credential-00000000-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void TaskReferences_RejectPartialHexMatches()
    {
        // A hex run longer than a guid must not half-match into one.
        var longHex = string.Concat(Enumerable.Repeat("ab", 20));
        Assert.Empty(TaskReferences.Extract($"@task-{longHex}"));
    }

    [Fact]
    public void Comment_EnforcesBounds()
    {
        var engagement = EngagementId.New();
        Assert.Throws<ArgumentException>(
            () => Comment.Create(CommentId.New(), engagement, CommentSubjectKind.Engagement, engagement.Value, new(Guid.NewGuid()), "   ", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(
            () => Comment.Create(CommentId.New(), engagement, CommentSubjectKind.Engagement, Guid.Empty, new(Guid.NewGuid()), "body", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(
            () => Comment.Create(CommentId.New(), engagement, CommentSubjectKind.Engagement, engagement.Value, new(Guid.NewGuid()), new string('x', CollaborationLimits.MaxCommentChars + 1), DateTimeOffset.UtcNow));

        var comment = Comment.Create(
            CommentId.New(), engagement, CommentSubjectKind.Engagement, engagement.Value, new(Guid.NewGuid()), "  trimmed  ", DateTimeOffset.UtcNow);
        Assert.Equal("trimmed", comment.Body);
        Assert.Null(comment.EditedAt);
        comment.Edit("revised", DateTimeOffset.UtcNow);
        Assert.Equal("revised", comment.Body);
        Assert.NotNull(comment.EditedAt);
    }

    [Fact]
    public async Task ReferenceService_UnknownAndForeignTokens_DoNotResolve()
    {
        var engagement = EngagementId.New();
        var tasks = new InMemoryTaskRepository();
        var credentials = new InMemoryCredentialStore();
        var service = new TaskReferenceService(tasks, credentials);

        var mine = await IssueAsync(tasks, engagement, "shell.exec");
        var foreign = await IssueAsync(tasks, EngagementId.New(), "shell.exec");
        var credential = await CaptureAsync(credentials, engagement, mine);

        var unresolved = await service.UnresolvedAsync(
            engagement, $"a @task-{mine.Id} b $cred-{credential.Id} c @task-{foreign.Id} d $cred-{Guid.NewGuid()}");
        // The engagement's own task and credential resolve; a foreign task
        // and an unknown credential do not.
        Assert.Equal(2, unresolved.Count);
        Assert.DoesNotContain(unresolved, t => t.Id == mine.Id.Value);
        Assert.DoesNotContain(unresolved, t => t.Id == credential.Id.Value);
    }

    [Fact]
    public async Task ReferenceService_SubstitutesSecrets_ByMatchedSpan()
    {
        var engagement = EngagementId.New();
        var tasks = new InMemoryTaskRepository();
        var credentials = new InMemoryCredentialStore();
        var service = new TaskReferenceService(tasks, credentials);

        var source = await IssueAsync(tasks, engagement, "collect.cred");
        var credential = await CaptureAsync(credentials, engagement, source, secret: "the-secret");

        // The N-form token in the text substitutes even though Render()
        // prints the hyphenated form: the replacement works by matched
        // span, not by string equality with a canonical form.
        var nForm = $"run $cred-{credential.Id.Value:N} now";
        Assert.Equal("run the-secret now", await service.SubstituteCredentialsAsync(engagement, nForm));

        var hyphenated = $"run $cred-{credential.Id.Value} now";
        Assert.Equal("run the-secret now", await service.SubstituteCredentialsAsync(engagement, hyphenated));

        // A credential with no secret material stays a placeholder -- a
        // silent empty substitution would hide the miss from the operator.
        var listed = await CaptureAsync(credentials, engagement, source, secret: null);
        var text = $"list $cred-{listed.Id.Value}";
        Assert.Equal(text, await service.SubstituteCredentialsAsync(engagement, text));
    }

    [Fact]
    public async Task Harvester_LandsRowsOnce_AndSkipsOtherVerbs()
    {
        var engagement = EngagementId.New();
        var tasks = new InMemoryTaskRepository();
        var credentials = new InMemoryCredentialStore();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var harvester = new CredentialHarvester(credentials, new DefaultVerbOutputResolver());

        var source = await IssueAsync(tasks, engagement, "collect.cred");
        var completed = Complete(source, "{\"kind\":\"password\",\"principal\":\"root\",\"secret\":\"s\"}");

        await harvester.CaptureAsync(completed);
        await harvester.CaptureAsync(completed); // Idempotent: the second run lands nothing.

        var rows = await credentials.ListByEngagementAsync(engagement);
        var row = Assert.Single(rows);
        Assert.Equal("root", row.Principal);
        Assert.Equal(source.Id, row.SourceTaskId);

        // A text verb's completion harvests nothing even with parseable
        // lines in its output.
        var textTask = await IssueAsync(tasks, engagement, "shell.exec");
        var textCompleted = Complete(textTask, "{\"kind\":\"password\",\"principal\":\"ghost\"}");
        await harvester.CaptureAsync(textCompleted);
        Assert.Single(await credentials.ListByEngagementAsync(engagement));
    }

    [Fact]
    public async Task Issuance_RefusesDanglingReference_BeforeTheQueue()
    {
        var tasks = new InMemoryTaskRepository();
        var implants = new InMemoryImplantRepository();
        var engagement = EngagementId.New();
        var implant = Implant.Enroll(ImplantId.New(), engagement, DateTimeOffset.UtcNow.AddDays(30), ImplantClass.Implant, DateTimeOffset.UtcNow);
        await implants.SaveAsync(implant);

        var references = new TaskReferenceService(tasks, new InMemoryCredentialStore());
        var service = new TaskService(
            tasks, implants, new InMemoryEngagementRepository(), TimeProvider.System,
            bus: null, capabilities: new ClassTableCapabilityResolver(), wake: null,
            references, harvester: null);

        var refused = await Assert.ThrowsAsync<TaskRejectedException>(
            () => service.IssueAsync(new IssueTaskCommand(
                engagement, implant.Id, OperatorId.New(), "shell.exec",
                $"$cred-{Guid.NewGuid()}")));
        Assert.Equal(TaskRejectionReason.UnknownTaskReference, refused.Reason);

        // A well-formed reference passes the gate and keeps its placeholder
        // in the queued task's stored arguments.
        var source = await service.IssueAsync(new IssueTaskCommand(
            engagement, implant.Id, OperatorId.New(), "shell.exec", "whoami"));
        var second = await service.IssueAsync(new IssueTaskCommand(
            engagement, implant.Id, OperatorId.New(), "shell.exec",
            $"follows @task-{source.TaskId}"));
        Assert.Contains($"@task-{source.TaskId}", second.Arguments);
    }

    [Fact]
    public async Task CommentStore_PagesByNewestWindow()
    {
        var engagement = EngagementId.New();
        var store = new InMemoryCommentStore();
        var author = new OperatorId(Guid.NewGuid());
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);

        var created = new List<Comment>();
        for (var i = 0; i < 5; i++)
        {
            var comment = Comment.Create(
                CommentId.New(), engagement, CommentSubjectKind.Engagement, engagement.Value,
                author, $"c{i}", DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(i));
            created.Add(comment);
            await store.SaveAsync(comment);
        }

        // Newest window first; the cursor walks one page older; items read
        // oldest-first within the page -- the audit listing's own contract.
        var page = await store.ListPageAsync(engagement, 2, null, null);
        Assert.Equal(new[] { "c3", "c4" }, page.Items.Select(c => c.Body));
        Assert.True(page.HasMore);

        DateTimeOffset? before = page.Items[0].CreatedAt;
        Guid? beforeId = page.Items[0].Id.Value;
        page = await store.ListPageAsync(engagement, 2, before, beforeId);
        Assert.Equal(new[] { "c1", "c2" }, page.Items.Select(c => c.Body));
        Assert.True(page.HasMore);

        before = page.Items[0].CreatedAt;
        beforeId = page.Items[0].Id.Value;
        page = await store.ListPageAsync(engagement, 2, before, beforeId);
        Assert.Equal(new[] { "c0" }, page.Items.Select(c => c.Body));
        Assert.False(page.HasMore);
    }

    private static async Task<Rod.CoreState.Tasks.Task> IssueAsync(InMemoryTaskRepository tasks, EngagementId engagement, string verb)
    {
        var task = Rod.CoreState.Tasks.Task.Create(
            TaskId.New(), engagement, ImplantId.New(), new OperatorId(Guid.NewGuid()),
            verb, "", DateTimeOffset.UtcNow);
        await tasks.SaveAsync(task);
        return task;
    }

    private static async Task<CredentialRecord> CaptureAsync(
        InMemoryCredentialStore store, EngagementId engagement, Rod.CoreState.Tasks.Task source, string? secret = "stored")
    {
        var record = new CredentialRecord(
            CredentialId.New(), engagement, source.Id, ImplantId.New(),
            "password", "principal", secret, null, null, null, DateTimeOffset.UtcNow);
        await store.AddAsync(record);
        return record;
    }

    private static TaskCompleted Complete(Rod.CoreState.Tasks.Task task, string output)
        => new(
            task.Id,
            task.EngagementId,
            task.ImplantId,
            task.IssuedBy,
            task.Verb,
            task.Arguments,
            output,
            TaskOutcome.Succeeded,
            DateTimeOffset.UtcNow);
}
