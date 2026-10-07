using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Task = System.Threading.Tasks.Task;
using static Rod.Integration.Tests.EngagementSetup;

namespace Rod.Integration.Tests;

/// <summary>
/// The task-snippet operator surface: snippets live behind the operator
/// front's authentication, manage through the engagement-scoped routes,
/// refuse what could never replay (a taken name, an over-bound shape), and
/// never leak across engagements -- a snippet saved in one engagement is
/// invisible and undeletable from another. Save and delete land in the
/// engagement trail attributed to the acting operator.
/// </summary>
public class TaskSnippetEndpointTests
{
    [Fact]
    public async Task Snippets_ManageThroughTheOperatorApi()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var create = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/task-snippets",
                new
                {
                    Name = "triage-sweep",
                    Steps = new[]
                    {
                        new { Verb = "recon.hostenum", Arguments = "" },
                        new { Verb = "recon.ps", Arguments = "" },
                        new { Verb = "collect.screenshot", Arguments = "" },
                    },
                });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var snippet = await create.Content.ReadFromJsonAsync<SnippetBody>();
            Assert.NotNull(snippet);
            Assert.Equal("triage-sweep", snippet!.Name);
            Assert.Equal(3, snippet.Steps.Count);
            Assert.Equal("recon.hostenum", snippet.Steps[0].Verb);
            Assert.Equal("collect.screenshot", snippet.Steps[2].Verb);

            var fetched = await client.GetFromJsonAsync<SnippetBody>(
                $"/engagements/{engagementId}/task-snippets/{snippet.Id}");
            Assert.NotNull(fetched);
            Assert.Equal("triage-sweep", fetched!.Name);

            var listed = await client.GetFromJsonAsync<SnippetListBody>(
                $"/engagements/{engagementId}/task-snippets");
            Assert.Single(listed!.Snippets);

            var audit = host.Services.GetRequiredService<IAuditStore>();
            var trail = await audit.ListAsync(Guid.Parse(engagementId));
            var saved = trail.Single(e => e.Kind == AuditEventKind.TaskSnippetSaved);
            Assert.Equal(operatorId.Value, saved.OperatorId);
            Assert.Equal(snippet.Id, saved.Outcome);

            var deleted = await client.DeleteAsync(
                $"/engagements/{engagementId}/task-snippets/{snippet.Id}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            var empty = await client.GetFromJsonAsync<SnippetListBody>(
                $"/engagements/{engagementId}/task-snippets");
            Assert.Empty(empty!.Snippets);

            trail = await audit.ListAsync(Guid.Parse(engagementId));
            Assert.Contains(trail, e => e.Kind == AuditEventKind.TaskSnippetDeleted
                && e.Outcome == snippet.Id);
        }
    }

    [Fact]
    public async Task Create_RefusesWhatCouldNeverReplay()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            async Task<HttpStatusCode> CreateAsync(object body)
            {
                var response = await client.PostAsJsonAsync(
                    $"/engagements/{engagementId}/task-snippets", body);
                return response.StatusCode;
            }

            // A taken name is refused (the palette reaches by name); a
            // missing name or an empty step list is malformed, not refused.
            var first = await CreateAsync(new
            {
                Name = "sweep",
                Steps = new[] { new { Verb = "recon.ps", Arguments = "" } },
            });
            Assert.Equal(HttpStatusCode.Created, first);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, await CreateAsync(new
            {
                Name = "sweep",
                Steps = new[] { new { Verb = "recon.ps", Arguments = "" } },
            }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, await CreateAsync(new
            {
                Name = "SWEEP",
                Steps = new[] { new { Verb = "recon.ps", Arguments = "" } },
            }));
            Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new
            {
                Name = "",
                Steps = new[] { new { Verb = "recon.ps", Arguments = "" } },
            }));
            Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new
            {
                Name = "empty",
                Steps = Array.Empty<object>(),
            }));
            // The step ceiling is a domain bound: over it is a well-formed
            // request the server declines with the reason.
            Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new
            {
                Name = "too-long",
                Steps = Enumerable.Range(0, 21).Select(_ => new { Verb = "recon.ps", Arguments = "" }).ToArray(),
            }));
            var foreign = await client.PostAsJsonAsync(
                $"/engagements/{Guid.NewGuid()}/task-snippets",
                new { Name = "x", Steps = new[] { new { Verb = "recon.ps", Arguments = "" } } });
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }
    }

    [Fact]
    public async Task Snippets_NeverCrossEngagements()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var otherEngagementId = await CreateEngagementAsync(client);

            var create = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/task-snippets",
                new
                {
                    Name = "sweep",
                    Steps = new[] { new { Verb = "recon.ps", Arguments = "" } },
                });
            var snippet = await create.Content.ReadFromJsonAsync<SnippetBody>();

            // The read and the delete are scoped: the snippet does not
            // exist from the other engagement's routes.
            var fetched = await client.GetAsync(
                $"/engagements/{otherEngagementId}/task-snippets/{snippet!.Id}");
            Assert.Equal(HttpStatusCode.NotFound, fetched.StatusCode);
            var otherList = await client.GetFromJsonAsync<SnippetListBody>(
                $"/engagements/{otherEngagementId}/task-snippets");
            Assert.Empty(otherList!.Snippets);
            var deleted = await client.DeleteAsync(
                $"/engagements/{otherEngagementId}/task-snippets/{snippet.Id}");
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);

            // It is still there through its own engagement.
            var mine = await client.GetFromJsonAsync<SnippetListBody>(
                $"/engagements/{engagementId}/task-snippets");
            Assert.Single(mine!.Snippets);
        }
    }

    private sealed record StepBody(string Verb, string Arguments);

    private sealed record SnippetBody(
        string Id,
        string EngagementId,
        string Name,
        System.Collections.Generic.IReadOnlyList<StepBody> Steps,
        DateTimeOffset CreatedAt,
        string CreatedBy);

    private sealed record SnippetListBody(System.Collections.Generic.IReadOnlyList<SnippetBody> Snippets);
}
