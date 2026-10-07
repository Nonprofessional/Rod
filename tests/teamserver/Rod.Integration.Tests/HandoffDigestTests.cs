using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// The shift handoff digest acceptance (architecture.md Sec 11.1): a
/// time-windowed, kind-whitelisted, ordered account of the watch, served by
/// the reporting layer the timeline and closeout export share. Trail facts are
/// appended directly with chosen timestamps -- the projection is under test
/// here, not the producers -- and every read goes over HTTP like an
/// operator's.
/// </summary>
public class HandoffDigestTests
{
    private static (HttpClient Client, IHost Host, OperatorId Operator) CreateClient()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        return (client, host, operatorId);
    }

    private static async Task<(Guid Engagement, DateTimeOffset Now)> CreateEngagementAsync(
        HttpClient client, IHost host)
    {
        await AuthenticatedHost.LoginAsync(client);
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Nightwatch"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        var now = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        return (Guid.Parse(created!.EngagementId), now);
    }

    private static async Task AppendFactAsync(
        IHost host, Guid engagement, AuditEventKind kind, DateTimeOffset at,
        Guid operatorId = default, Guid implantId = default)
    {
        var audit = host.Services.GetRequiredService<IAuditStore>();
        await audit.AppendAsync(AuditEvent.Fact(
            eventId: Guid.NewGuid(),
            engagementId: engagement,
            operatorId: operatorId,
            implantId: implantId,
            taskId: Guid.Empty,
            verb: "digest-test",
            kind: kind,
            payload: "a digest test fact",
            output: null,
            outcome: "test",
            at: at));
    }

    [Fact]
    public async Task Digest_SelectsTheWatchsBeats_FromTheDefaultWindow()
    {
        var (client, host, operatorId) = CreateClient();
        using (client)
        using (host)
        {
            var (engagement, now) = await CreateEngagementAsync(client, host);

            // Inside the default twelve-hour window, whitelisted, in trail
            // (chronological) order.
            await AppendFactAsync(host, engagement, AuditEventKind.SessionOpened,
                now.AddHours(-2), operatorId.Value, implantId: Guid.NewGuid());
            await AppendFactAsync(host, engagement, AuditEventKind.SessionClosed, now.AddMinutes(-90));
            await AppendFactAsync(host, engagement, AuditEventKind.TaskIssued,
                now.AddMinutes(-80), operatorId.Value);
            await AppendFactAsync(host, engagement, AuditEventKind.TaskCompleted, now.AddMinutes(-70));
            await AppendFactAsync(host, engagement, AuditEventKind.ImplantNoteAdded,
                now.AddMinutes(-60), operatorId.Value);

            // Outside the window: yesterday's session.
            await AppendFactAsync(host, engagement, AuditEventKind.SessionOpened, now.AddHours(-30));
            // Inside the window, not the watch's beats: plumbing and fetches.
            await AppendFactAsync(host, engagement, AuditEventKind.TaskDispatched, now.AddMinutes(-50));
            await AppendFactAsync(host, engagement, AuditEventKind.PayloadFetched, now.AddMinutes(-40));

            var digest = await client.GetFromJsonAsync<DigestBody>(
                $"/engagements/{engagement}/handoff-digest");

            Assert.NotNull(digest);
            Assert.Equal("Operation Nightwatch", digest!.EngagementName);
            // The window resolved to the defaults: to = now, from = now - 12h.
            Assert.True(digest.To >= now.AddSeconds(-5));
            Assert.Equal(digest.To.AddHours(-12), digest.From, TimeSpan.FromSeconds(5));

            var kinds = digest.Entries.Select(e => e.Kind).ToArray();
            Assert.Equal(
                new[] { "SessionOpened", "SessionClosed", "TaskIssued", "TaskCompleted", "ImplantNoteAdded" },
                kinds);

            var summary = digest.Summary!;
            Assert.Equal(1, summary.SessionsOpened);
            Assert.Equal(1, summary.SessionsClosed);
            Assert.Equal(0, summary.ImplantsEnrolled);
            Assert.Equal(1, summary.TasksIssued);
            Assert.Equal(1, summary.TasksCompleted);
            Assert.Equal(0, summary.TasksCancelled);
            Assert.Equal(0, summary.RoeRefusals);
            Assert.Equal(1, summary.NotesAdded);
            Assert.Equal(0, summary.ShellSessionsOpened);
            Assert.Equal(0, summary.ShellSessionsEnded);

            // Enrichment: the operator-attributed entry resolves the handle,
            // the system-attributed one carries no actor.
            var opened = digest.Entries[0];
            Assert.Equal("operator", opened.Operator!.Handle);
            Assert.NotNull(opened.Implant);
            Assert.Null(digest.Entries[1].Operator);

            Assert.True(digest.ChainVerified);
        }
    }

    [Fact]
    public async Task WindowBounds_AreInclusive()
    {
        var (client, host, operatorId) = CreateClient();
        using (client)
        using (host)
        {
            var (engagement, now) = await CreateEngagementAsync(client, host);
            var first = now.AddHours(-2);
            var last = now.AddHours(-1);
            await AppendFactAsync(host, engagement, AuditEventKind.SessionOpened, first, operatorId.Value);
            await AppendFactAsync(host, engagement, AuditEventKind.TaskCancelled, last, operatorId.Value);
            // Just outside on both ends.
            await AppendFactAsync(host, engagement, AuditEventKind.TaskCompleted, first.AddTicks(-1));
            await AppendFactAsync(host, engagement, AuditEventKind.TaskCompleted, last.AddTicks(1));

            var digest = await client.GetFromJsonAsync<DigestBody>(
                $"/engagements/{engagement}/handoff-digest" +
                $"?from={Uri.EscapeDataString(first.ToString("O"))}" +
                $"&to={Uri.EscapeDataString(last.ToString("O"))}");

            Assert.NotNull(digest);
            Assert.Equal(2, digest!.Entries.Count);
            Assert.Equal("SessionOpened", digest.Entries[0].Kind);
            Assert.Equal("TaskCancelled", digest.Entries[1].Kind);
        }
    }

    [Fact]
    public async Task SameWindow_Twice_IsReproducible()
    {
        var (client, host, operatorId) = CreateClient();
        using (client)
        using (host)
        {
            var (engagement, now) = await CreateEngagementAsync(client, host);
            await AppendFactAsync(host, engagement, AuditEventKind.TaskIssued,
                now.AddMinutes(-30), operatorId.Value);

            var from = Uri.EscapeDataString(now.AddHours(-6).ToString("O"));
            var to = Uri.EscapeDataString(now.ToString("O"));
            var first = await client.GetFromJsonAsync<DigestBody>(
                $"/engagements/{engagement}/handoff-digest?from={from}&to={to}");
            var second = await client.GetFromJsonAsync<DigestBody>(
                $"/engagements/{engagement}/handoff-digest?from={from}&to={to}");

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first!.ContentHash, second!.ContentHash);
            Assert.NotEqual(first.GeneratedAt, second.GeneratedAt);
        }
    }

    [Fact]
    public async Task Markdown_RendersTheOrderedAccount()
    {
        var (client, host, operatorId) = CreateClient();
        using (client)
        using (host)
        {
            var (engagement, now) = await CreateEngagementAsync(client, host);
            await AppendFactAsync(host, engagement, AuditEventKind.SessionOpened,
                now.AddMinutes(-30), operatorId.Value, implantId: Guid.NewGuid());
            await AppendFactAsync(host, engagement, AuditEventKind.TaskRoeRefused,
                now.AddMinutes(-20), operatorId.Value);

            var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
            var to = Uri.EscapeDataString(now.ToString("O"));
            var response = await client.GetAsync(
                $"/engagements/{engagement}/handoff-digest?from={from}&to={to}&format=markdown");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/markdown; charset=utf-8", response.Content.Headers.ContentType?.ToString());
            var markdown = await response.Content.ReadAsStringAsync();
            Assert.Contains("# Shift handoff digest: Operation Nightwatch", markdown);
            Assert.Contains("## The watch in numbers", markdown);
            Assert.Contains("## The watch in order", markdown);
            Assert.Contains("**SessionOpened**", markdown);
            Assert.Contains("**TaskRoeRefused**", markdown);
            Assert.Contains("by `operator`", markdown);
            Assert.Contains("ROE refusals: 1", markdown);
        }
    }

    [Fact]
    public async Task MalformedOrInvalidRequests_AreRefused()
    {
        var (client, host, operatorId) = CreateClient();
        using (client)
        using (host)
        {
            var (engagement, now) = await CreateEngagementAsync(client, host);
            var route = $"/engagements/{engagement}/handoff-digest";

            // Malformed engagement id, unknown engagement.
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync("/engagements/not-a-guid/handoff-digest")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync($"/engagements/{Guid.NewGuid()}/handoff-digest")).StatusCode);

            // Unparseable bounds.
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync($"{route}?from=yesterday")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync($"{route}?to=soon")).StatusCode);

            // Inverted and empty windows.
            var to = Uri.EscapeDataString(now.ToString("O"));
            var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync($"{route}?from={to}&to={from}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync($"{route}?from={to}&to={to}")).StatusCode);

            // A span beyond the cap.
            var farFrom = Uri.EscapeDataString(now.AddDays(-40).ToString("O"));
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync($"{route}?from={farFrom}&to={to}")).StatusCode);
        }
    }

    [Fact]
    public async Task AnonymousRequest_IsRefused()
    {
        var (client, host, _) = CreateClient();
        using (client)
        using (host)
        {
            var response = await client.GetAsync($"/engagements/{Guid.NewGuid()}/handoff-digest");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // Mutable read DTOs over the wire (System.Text.Json web defaults map the
    // server's camelCase onto these), the suite's established pattern.
    private sealed class DigestBody
    {
        public Guid EngagementId { get; set; }
        public string EngagementName { get; set; } = "";
        public DateTimeOffset From { get; set; }
        public DateTimeOffset To { get; set; }
        public DateTimeOffset GeneratedAt { get; set; }
        public string ContentHash { get; set; } = "";
        public bool ChainVerified { get; set; }
        public string? ChainBreak { get; set; }
        public SummaryBody? Summary { get; set; }
        public List<EntryBody> Entries { get; set; } = [];
    }

    private sealed class SummaryBody
    {
        public int SessionsOpened { get; set; }
        public int SessionsClosed { get; set; }
        public int ImplantsEnrolled { get; set; }
        public int ImplantsRetired { get; set; }
        public int TasksIssued { get; set; }
        public int TasksCompleted { get; set; }
        public int TasksCancelled { get; set; }
        public int RoeRefusals { get; set; }
        public int NotesAdded { get; set; }
        public int ShellSessionsOpened { get; set; }
        public int ShellSessionsEnded { get; set; }
    }

    private sealed class EntryBody
    {
        public Guid EventId { get; set; }
        public DateTimeOffset At { get; set; }
        public string Kind { get; set; } = "";
        public string Verb { get; set; } = "";
        public ActorBody? Operator { get; set; }
        public SubjectBody? Implant { get; set; }
        public string Payload { get; set; } = "";
        public string? Output { get; set; }
        public string Outcome { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    private sealed class ActorBody
    {
        public Guid OperatorId { get; set; }
        public string Handle { get; set; } = "";
    }

    private sealed class SubjectBody
    {
        public Guid ImplantId { get; set; }
        public string Class { get; set; } = "";
    }
}
