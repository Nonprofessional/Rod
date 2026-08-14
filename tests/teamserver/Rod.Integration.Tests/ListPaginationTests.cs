using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
using Rod.Transport.Endpoints;
// The domain entity shares its name with System.Threading.Tasks.Task; this file
// is async throughout, so pin Task to the BCL type and reach the entity by its
// full name.
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// List pagination acceptance (architecture.md Sec 4.3, Sec 10.3/11): the task,
/// audit, and artifact list endpoints accept a limit and an opaque cursor, and
/// walking the pages recovers the full history exactly once -- a long engagement
/// no longer grows any listing response without bound. Seeded through the
/// in-memory stores so the history sizes are under the test's control.
/// </summary>
public class ListPaginationTests
{
    [Fact]
    public async Task TaskList_WalksEveryPage_OnceEach()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagement = await CreateEngagementAsync(client);

            var tasks = host.Services.GetRequiredService<ITaskRepository>();
            var seeded = await SeedTasksAsync(tasks, engagement, count: 120);

            var seen = new List<string>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var query = $"/engagements/{engagement}/tasks?limit=50"
                    + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
                var page = await client.GetFromJsonAsync<TaskEndpoints.TaskListResponse>(query);
                Assert.NotNull(page);
                pages++;

                // Oldest first within the page.
                Assert.Equal(
                    page!.Items.Select(t => t.CreatedAt).ToArray(),
                    page.Items.Select(t => t.CreatedAt).OrderBy(at => at).ToArray());
                seen.AddRange(page.Items.Select(t => t.TaskId));
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            // Three pages, no duplicates, and the full history exactly once.
            Assert.Equal(3, pages);
            Assert.Equal(120, seen.Count);
            Assert.Equal(120, seen.Distinct().Count());
            Assert.Equal(
                seeded.Select(t => t.Id.ToString()).OrderBy(id => id).ToArray(),
                seen.OrderBy(id => id).ToArray());
        }
    }

    [Fact]
    public async Task AuditList_WalksEveryPage_OnceEach()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagement = await CreateEngagementAsync(client);

            var audit = host.Services.GetRequiredService<IAuditStore>();
            var seeded = await SeedEventsAsync(audit, Guid.Parse(engagement), count: 90);

            var seen = new List<Guid>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var query = $"/engagements/{engagement}/audit?limit=40"
                    + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
                var page = await client.GetFromJsonAsync<AuditEndpoints.AuditListResponse>(query);
                Assert.NotNull(page);
                pages++;

                Assert.Equal(
                    page!.Items.Select(e => e.At).ToArray(),
                    page.Items.Select(e => e.At).OrderBy(at => at).ToArray());
                seen.AddRange(page.Items.Select(e => e.EventId));
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            // 40 + 40 + 11 pages (the engagement-creation event sits on the
            // trail alongside the seeded 90), no duplicates, and every seeded
            // event reached exactly once.
            Assert.Equal(3, pages);
            Assert.Equal(91, seen.Count);
            Assert.Equal(91, seen.Distinct().Count());
            Assert.All(seeded, e => Assert.Contains(e.EventId, seen));
        }
    }

    [Fact]
    public async Task ArtifactList_WalksEveryPage_OnceEach()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagement = await CreateEngagementAsync(client);

            var artifacts = host.Services.GetRequiredService<IArtifactStore>();
            // The artifacts route resolves the owning task, so the seeded task
            // must exist in the task repository too.
            var task = (await SeedTasksAsync(
                host.Services.GetRequiredService<ITaskRepository>(), engagement, count: 1))[0];
            var taskId = task.Id.Value;
            var seeded = await SeedArtifactsAsync(artifacts, Guid.Parse(engagement), taskId, count: 25);

            var seen = new List<string>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var query = $"/engagements/{engagement}/tasks/{taskId:N}/artifacts?limit=10"
                    + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
                var page = await client.GetFromJsonAsync<ArtifactEndpoints.ArtifactListResponse>(query);
                Assert.NotNull(page);
                pages++;

                Assert.Equal(
                    page!.Items.Select(a => a.StoredAt).ToArray(),
                    page.Items.Select(a => a.StoredAt).OrderBy(at => at).ToArray());
                seen.AddRange(page.Items.Select(a => a.ArtifactId));
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            // 10 + 10 + 5 pages, no duplicates, the full set exactly once.
            Assert.Equal(3, pages);
            Assert.Equal(25, seen.Count);
            Assert.Equal(25, seen.Distinct().Count());
            Assert.Equal(
                seeded.Select(a => a.ArtifactId.ToString("N")).OrderBy(id => id).ToArray(),
                seen.OrderBy(id => id).ToArray());
        }
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("cursor=garbage")]
    public async Task Lists_Return400_ForInvalidPagingParameters(string badQuery)
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagement = await CreateEngagementAsync(client);

            var tasksResponse = await client.GetAsync($"/engagements/{engagement}/tasks?{badQuery}");
            var auditResponse = await client.GetAsync($"/engagements/{engagement}/audit?{badQuery}");
            var artifactsResponse = await client.GetAsync(
                $"/engagements/{engagement}/tasks/{Guid.NewGuid():N}/artifacts?{badQuery}");

            Assert.Equal(HttpStatusCode.BadRequest, tasksResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, auditResponse.StatusCode);
            // The artifacts route resolves the task only after paging binds, so
            // an invalid paging parameter is a 400 even for an unknown task.
            Assert.Equal(HttpStatusCode.BadRequest, artifactsResponse.StatusCode);
        }
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Ledger"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    private static readonly DateTimeOffset Base = DateTimeOffset.UnixEpoch;

    private static async Task<List<Rod.CoreState.Tasks.Task>> SeedTasksAsync(
        ITaskRepository tasks, string engagement, int count)
    {
        var engagementId = new EngagementId(Guid.Parse(engagement));
        var seeded = new List<Rod.CoreState.Tasks.Task>(count);
        for (var i = 0; i < count; i++)
        {
            var task = Rod.CoreState.Tasks.Task.Create(
                TaskId.New(), engagementId, ImplantId.New(), OperatorId.New(),
                "shell.exec", string.Empty, Base.AddMinutes(i));
            await tasks.SaveAsync(task);
            seeded.Add(task);
        }

        return seeded;
    }

    private static async Task<List<AuditEvent>> SeedEventsAsync(
        IAuditStore audit, Guid engagement, int count)
    {
        var seeded = new List<AuditEvent>(count);
        for (var i = 0; i < count; i++)
        {
            var @event = AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagement,
                operatorId: Guid.NewGuid(),
                implantId: Guid.NewGuid(),
                taskId: Guid.NewGuid(),
                verb: "shell.exec",
                kind: AuditEventKind.TaskCompleted,
                payload: $"arg-{i}",
                output: "out",
                outcome: "Succeeded",
                at: Base.AddSeconds(i));
            await audit.AppendAsync(@event);
            seeded.Add(@event);
        }

        return seeded;
    }

    private static async Task<List<Artifact>> SeedArtifactsAsync(
        IArtifactStore artifacts, Guid engagement, Guid task, int count)
    {
        var seeded = new List<Artifact>(count);
        for (var i = 0; i < count; i++)
        {
            var artifact = new Artifact(
                ArtifactId: Guid.NewGuid(),
                EngagementId: engagement,
                TaskId: task,
                OperatorId: Guid.NewGuid(),
                Name: $"artifact-{i}",
                ContentType: "text/plain",
                Content: new byte[] { (byte)i },
                Size: 1,
                StoredAt: Base.AddSeconds(i));
            await artifacts.SaveAsync(artifact);
            seeded.Add(artifact);
        }

        return seeded;
    }
}
