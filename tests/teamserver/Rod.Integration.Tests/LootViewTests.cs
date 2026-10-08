using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Rod.Audit;
using Rod.Transport.Endpoints;
using static Rod.Integration.Tests.EngagementSetup;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: the typed loot view (architecture.md Sec 11.2). The collection
/// verbs already land their captures in the artifact store, bound to the task
/// and verb by ExfilCaptured/ArtifactAttached events; the loot view is the
/// organizer over them -- an engagement-wide paged listing classified by the
/// producing verb and content type, each entry carrying its capture
/// attribution. Opening a piece of loot (retrieving the bytes) records an
/// ArtifactViewed event attributed to the downloading operator: reading a
/// projection is not an act on the engagement, but evidence leaving the
/// platform is.
/// </summary>
public sealed class LootViewTests
{
    [Fact]
    public async Task Loot_ClassifiesByProducingVerbAndContentType_AndPages()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintDeployTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            // Three tasks gathering three kinds of loot: a screenshot task,
            // a credential task, and a file pull.
            async Task<string> AttachAsync(string verb, string name, string contentType)
            {
                var issued = await client.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks",
                    new TaskEndpoints.IssueTaskRequest(ImplantId: implantId, Verb: verb, Arguments: "operand"));
                issued.EnsureSuccessStatusCode();
                var task = await issued.Content.ReadFromJsonAsync<TaskEndpoints.TaskResponse>();

                var attached = await client.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks/{task!.TaskId}/artifacts",
                    new ArtifactEndpoints.AttachArtifactRequest(
                        Name: name, ContentType: contentType, Content: [0x01, 0x02]));
                attached.EnsureSuccessStatusCode();
                return (await attached.Content.ReadFromJsonAsync<ArtifactEndpoints.ArtifactResponse>())!
                    .ArtifactId;
            }

            var screenshotId = await AttachAsync("collect.screenshot", "screen.png", "image/png");
            var credId = await AttachAsync("collect.cred", "stores.txt", "text/plain");
            var fileId = await AttachAsync("file.pull", "passwd.txt", "text/plain");
            // An operator attach on a shell task: no collection verb, a plain
            // text type -- "other" keeps the view complete rather than
            // dropping what the vocabulary has not met.
            var otherId = await AttachAsync("shell.exec", "console.log", "text/plain");

            // The unfiltered view lists everything, newest window first.
            var all = await client.GetFromJsonAsync<ArtifactEndpoints.LootListResponse>(
                $"/engagements/{engagementId}/loot");
            Assert.NotNull(all);
            Assert.Null(all!.NextCursor);
            Assert.Equal(4, all.Items.Length);
            Assert.Contains(all.Items, i => i.ArtifactId == screenshotId && i.Kind == "screenshot");
            Assert.Contains(all.Items, i => i.ArtifactId == credId && i.Kind == "credential");
            Assert.Contains(all.Items, i => i.ArtifactId == fileId && i.Kind == "file");
            Assert.Contains(all.Items, i => i.ArtifactId == otherId && i.Kind == "other");

            // Every entry carries its capture attribution: the task, its verb,
            // the implant, and the operator the artifact credits.
            var screenshot = all.Items.Single(i => i.ArtifactId == screenshotId);
            Assert.Equal("collect.screenshot", screenshot.Verb);
            Assert.Equal(implantId, screenshot.ImplantId);
            Assert.Equal(operatorId.Value, screenshot.CapturedBy);

            // The kind filters select one vocabulary slot each.
            var shots = await client.GetFromJsonAsync<ArtifactEndpoints.LootListResponse>(
                $"/engagements/{engagementId}/loot?kind=screenshot");
            var shot = Assert.Single(shots!.Items);
            Assert.Equal(screenshotId, shot.ArtifactId);

            var files = await client.GetFromJsonAsync<ArtifactEndpoints.LootListResponse>(
                $"/engagements/{engagementId}/loot?kind=file");
            var file = Assert.Single(files!.Items);
            Assert.Equal(fileId, file.ArtifactId);

            // An unknown kind is a 400, not a silently empty view.
            var badKind = await client.GetAsync($"/engagements/{engagementId}/loot?kind=nope");
            Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);

            // A foreign engagement is the scoped-read 404 ladder.
            var foreign = await client.GetAsync($"/engagements/{Guid.NewGuid()}/loot");
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }
    }

    [Fact]
    public async Task Loot_PagesTheEngagementWideWalk()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintDeployTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(ImplantId: implantId, Verb: "file.pull", Arguments: "operand"));
            issued.EnsureSuccessStatusCode();
            var task = await issued.Content.ReadFromJsonAsync<TaskEndpoints.TaskResponse>();

            for (var i = 0; i < 3; i++)
            {
                var attached = await client.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks/{task!.TaskId}/artifacts",
                    new ArtifactEndpoints.AttachArtifactRequest(
                        Name: $"loot-{i}", ContentType: "text/plain", Content: new byte[] { (byte)i }));
                attached.EnsureSuccessStatusCode();
            }

            // Walk the whole engagement two records at a time: pages overlap
            // nothing, the cursor walks strictly older, and the walk ends.
            var seen = new List<string>();
            string? cursor = null;
            do
            {
                var page = await client.GetFromJsonAsync<ArtifactEndpoints.LootListResponse>(
                    $"/engagements/{engagementId}/loot?limit=2{(cursor is null ? "" : $"&cursor={cursor}")}");
                seen.AddRange(page!.Items.Select(i => i.ArtifactId));
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            Assert.Equal(3, seen.Count);
            Assert.Equal(3, seen.Distinct().Count());
        }
    }

    [Fact]
    public async Task LootOpen_RecordsAttributedArtifactViewedEvent()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintDeployTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(
                    ImplantId: implantId, Verb: "collect.screenshot", Arguments: "operand"));
            issued.EnsureSuccessStatusCode();
            var task = await issued.Content.ReadFromJsonAsync<TaskEndpoints.TaskResponse>();

            var attached = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks/{task!.TaskId}/artifacts",
                new ArtifactEndpoints.AttachArtifactRequest(
                    Name: "screen.png", ContentType: "image/png", Content: [0x89, 0x50, 0x4e, 0x47]));
            attached.EnsureSuccessStatusCode();
            var artifact = await attached.Content.ReadFromJsonAsync<ArtifactEndpoints.ArtifactResponse>();

            // Open the captured screenshot from the loot view -- retrieve its
            // bytes. The AC's leg: the open carries attribution in the audit
            // trail.
            var opened = await client.GetAsync(
                $"/engagements/{engagementId}/artifacts/{artifact!.ArtifactId}");
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

            var audit = host.Services.GetRequiredService<IAuditStore>();
            var viewed = (await audit.ListAsync(Guid.Parse(engagementId)))
                .Where(e => e.Kind == AuditEventKind.ArtifactViewed)
                .ToArray();
            var @event = Assert.Single(viewed);
            Assert.Equal(operatorId.Value, @event.OperatorId);
            Assert.Equal(Guid.Parse(task.TaskId), @event.TaskId);
            Assert.Equal(Guid.Parse(implantId), @event.ImplantId);
            Assert.Equal(artifact.ArtifactId, @event.Outcome);
            Assert.Equal("screen.png;image/png", @event.Payload);
        }
    }
}
