using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Task = System.Threading.Tasks.Task;
using static Rod.Integration.Tests.EngagementSetup;

namespace Rod.Integration.Tests;

/// <summary>
/// The console-depth acceptance criterion (docs/todo.md, the solo-operator
/// item): an operator saves a named task snippet once and issues its whole
/// sequence with one command from the palette. The palette's run path is
/// replay in the console: look the snippet up by name, then issue every
/// step through the ordinary tasking route the keyboard would reach -- so
/// the proof drives exactly that path against the real API and asserts the
/// sequence lands as attributed tasking: every step queued verbatim, every
/// TaskIssued attributed to the running operator, and the save itself on
/// the engagement's audit trail.
/// </summary>
public class TaskSnippetAcceptanceTests
{
    [Fact]
    public async Task ASavedSnippet_IssuesItsWholeSequenceFromOneCommand()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var implantId = await EnrollAsync(client, await MintDeployTokenAsync(client, engagementId));

            // Saved once, by name.
            var create = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/task-snippets",
                new
                {
                    Name = "triage",
                    Steps = new[]
                    {
                        new { Verb = "recon.hostenum", Arguments = "" },
                        new { Verb = "recon.ps", Arguments = "" },
                        new { Verb = "collect.screenshot", Arguments = "" },
                    },
                });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            // One command from the palette: the lookup by name, then the
            // whole sequence issued through the ordinary tasking route --
            // the exact requests the console's run path makes.
            var listed = await client.GetFromJsonAsync<SnippetListBody>(
                $"/engagements/{engagementId}/task-snippets");
            var snippet = listed!.Snippets.Single(s => s.Name == "triage");
            foreach (var step in snippet.Steps)
            {
                var issued = await client.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks",
                    new { ImplantId = implantId, Verb = step.Verb, Arguments = step.Arguments });
                Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
            }

            // The sequence landed: every step queued verbatim, in order.
            var tasks = await client.GetFromJsonAsync<TaskPageBody>(
                $"/engagements/{engagementId}/implants/{implantId}/tasks");
            var verbs = tasks!.Items.Select(t => t.Verb).ToArray();
            Assert.Equal(new[] { "recon.hostenum", "recon.ps", "collect.screenshot" }, verbs);

            // And the trail holds the whole story: the save attributed to
            // the operator, and each TaskIssued beside it -- running writes
            // no record of its own, the tasks are the trail.
            var audit = host.Services.GetRequiredService<IAuditStore>();
            var trail = await audit.ListAsync(Guid.Parse(engagementId));
            Assert.Contains(trail, e => e.Kind == AuditEventKind.TaskSnippetSaved
                && e.OperatorId == operatorId.Value
                && e.Payload.Contains("triage"));
            var issuedEvents = trail.Where(e => e.Kind == AuditEventKind.TaskIssued).ToArray();
            Assert.Equal(3, issuedEvents.Length);
            Assert.All(issuedEvents, e => Assert.Equal(operatorId.Value, e.OperatorId));
        }
    }

    private sealed record StepBody(string Verb, string Arguments);

    private sealed record SnippetBody(string Id, string Name, System.Collections.Generic.IReadOnlyList<StepBody> Steps);

    private sealed record SnippetListBody(System.Collections.Generic.IReadOnlyList<SnippetBody> Snippets);

    private sealed record TaskBody(string TaskId, string Verb, string Status);

    private sealed record TaskPageBody(System.Collections.Generic.IReadOnlyList<TaskBody> Items, string? NextCursor);
}
