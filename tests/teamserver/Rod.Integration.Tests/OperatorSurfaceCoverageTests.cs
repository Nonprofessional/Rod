using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.Operators;
using Rod.Tradecraft;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Evasion;
using Rod.Tradecraft.Exploit;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: recon through exploit are issuable from the
/// operator surface, and the evidence views (audit, artifacts, timeline,
/// report) are reachable, and the OPSEC controls (implant retire, redirector
/// repoint) are exposed. Drives the operator-facing HTTP API through the
/// in-memory TestServer with the tradecraft and operator layers layered onto the
/// transport core -- the same composition the teamserver host performs.
/// </summary>
/// <remarks>
/// The capability catalog endpoint (<c>GET /capabilities</c>) and the
/// engagement-wide task list (<c>GET /engagements/{id}/tasks</c>) are what keep
/// the UI data-driven rather than hardcoding the verb table; the rest of the
/// surface (audit, artifact, timeline, report, retire, repoint) predates
/// them, and this test confirms the operator can reach it end to end. The
/// assertion is *issuable*, not
/// *executable*: every verb is accepted for tasking (201); the placeholder
/// modules fail on dispatch, which is correct -- concrete tradecraft is
/// out-of-tree (architecture.md Sec 13, AGENTS.md Sec 7).
/// </remarks>
public class OperatorSurfaceCoverageTests
{
    // A host that layers the tradecraft layer, the operator + auth layers,
    // and the capability-catalog endpoint onto the transport core -- the
    // same composition the teamserver host performs. Mirrors the fixture in
    // TradecraftTaskPathTests; the additions over the authenticated host are
    // AddRodTradecraft and MapCapabilityEndpoints.
    private static (HttpClient Client, IHost Host) CreateClient()
    {
        var (client, host, _) = AuthenticatedHost.Create(
            configureServices: services => services.AddRodTradecraft(),
            mapEndpoints: endpoints => endpoints.MapCapabilityEndpoints());
        // Every test drives the operator API, so establish the session up front.
        AuthenticatedHost.LoginAsync(client).GetAwaiter().GetResult();
        return (client, host);
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Lantern"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    // Enrolls a Stage-2 implant directly through the registry so the task gate
    // has a target across every capability category. Stage-2 carries the full
    // class-gated verb set; evasion and exploit are not class-gated and the
    // registry-backed resolver admits them on any class.
    private static async Task<Implant> EnrollStage2Async(IHost host, EngagementId engagement)
    {
        var implants = host.Services.GetRequiredService<IImplantRepository>();
        var clock = host.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), engagement, now.AddDays(30), ImplantClass.Stage2, now);
        await implants.SaveAsync(implant);
        return implant;
    }

    [Fact]
    public async Task CapabilityCatalog_ListsEveryCategoryAndBuiltInVerbs()
    {
        // The catalog is what lets the UI surface the full capability set without
        // a hardcoded verb table. It spans all eight categories (core, recon,
        // lateral, persist, collect, exfil, evasion, exploit) and carries the
        // built-in verbs the task path admits.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var descriptors = await client.GetFromJsonAsync<CapabilityDescriptorResponse[]>("/capabilities");
            Assert.NotNull(descriptors);
            var categories = descriptors!.Select(d => d.Category).ToHashSet();
            foreach (var expected in new[]
            {
                nameof(CapabilityCategory.Core),
                nameof(CapabilityCategory.Recon),
                nameof(CapabilityCategory.Lateral),
                nameof(CapabilityCategory.Persist),
                nameof(CapabilityCategory.Collect),
                nameof(CapabilityCategory.Exfil),
                nameof(CapabilityCategory.Evasion),
                nameof(CapabilityCategory.Exploit),
            })
            {
                Assert.Contains(expected, categories);
            }

            var verbs = descriptors.Select(d => d.Verb).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("shell.exec", verbs);
            Assert.Contains("recon.portscan", verbs);
            Assert.Contains(EvasionCapabilities.Avoid, verbs);
            Assert.Contains(ExploitCapabilities.Invoke, verbs);
        }
    }

    // One representative verb per capability category: core, recon, lateral,
    // persist, collect, exfil, evasion, exploit. Each must be issuable from the
    // operator surface (the AC: recon through exploit are issuable).
    public static readonly IEnumerable<object[]> RepresentativeVerbs = new[]
    {
        new object[] { "shell.exec" },
        new object[] { "recon.portscan" },
        new object[] { "lateral.move" },
        new object[] { "persist.install" },
        new object[] { "file.pull" },
        new object[] { "exfil.push" },
        new object[] { EvasionCapabilities.Avoid },
        new object[] { ExploitCapabilities.Invoke },
    };

    [Theory]
    [MemberData(nameof(RepresentativeVerbs))]
    public async Task EveryCapabilityCategory_IsIssuable(string verb)
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagementId = await CreateEngagementAsync(client);
            var implant = await EnrollStage2Async(host, new EngagementId(Guid.Parse(engagementId)));

            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(
                    ImplantId: implant.Id.ToString(),
                    Verb: verb,
                    Arguments: "operand"));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task EngagementWideTaskList_AuditTrail_TimelineAndReport_AreReachable()
    {
        // The evidence half of the AC: after tasking, the engagement-wide task
        // list, the audit trail, and the timeline/report exports
        // all carry the issued task over HTTP.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagementId = await CreateEngagementAsync(client);
            var implant = await EnrollStage2Async(host, new EngagementId(Guid.Parse(engagementId)));

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(
                    ImplantId: implant.Id.ToString(),
                    Verb: "recon.portscan",
                    Arguments: "10.0.0.0/24"));
            issued.EnsureSuccessStatusCode();

            // Engagement-wide task list: the issued verb appears across the
            // engagement, not just under one implant.
            var tasks = await client.GetFromJsonAsync<TaskEndpoints.TaskListResponse>(
                $"/engagements/{engagementId}/tasks");
            Assert.NotNull(tasks);
            Assert.Contains(tasks!.Items, t => t.Verb == "recon.portscan" && t.ImplantId == implant.Id.ToString());

            // Audit trail: a TaskIssued event is attributed to the action.
            var audit = await client.GetFromJsonAsync<AuditEndpoints.AuditListResponse>(
                $"/engagements/{engagementId}/audit");
            Assert.NotNull(audit);
            Assert.Contains(audit!.Items, e => e.Kind == nameof(Rod.Audit.AuditEventKind.TaskIssued));

            // Timeline export: a reproducible, content-hashed timeline.
            var timeline = await client.GetFromJsonAsync<TimelineReportResponse>(
                $"/engagements/{engagementId}/timeline");
            Assert.NotNull(timeline);
            Assert.NotEmpty(timeline!.Entries);
            Assert.False(string.IsNullOrWhiteSpace(timeline.ContentHash));

            // Report export: the full engagement bundle (operators, implants,
            // tasks, artifacts, timeline).
            var report = await client.GetFromJsonAsync<EngagementReportResponse>(
                $"/engagements/{engagementId}/report");
            Assert.NotNull(report);
            Assert.NotEmpty(report!.Tasks);
            Assert.NotEmpty(report.Timeline);
        }
    }

    [Fact]
    public async Task OpsecControls_ImplantRetireAndRedirectorRepoint_AreExposed()
    {
        // The  OPSEC controls are reachable from the operator surface: retire
        // (burn) an implant, and repoint (swap) a listener's public endpoint --
        // the redirector implants dial -- without touching the backend.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagementId = await CreateEngagementAsync(client);
            var implant = await EnrollStage2Async(host, new EngagementId(Guid.Parse(engagementId)));

            // Retire the implant: it is taken out of operation and its retirement
            // is reflected in the listing.
            var retireResponse = await client.PostAsync(
                $"/engagements/{engagementId}/implants/{implant.Id}:retire",
                content: null);
            Assert.Equal(HttpStatusCode.OK, retireResponse.StatusCode);
            var retired = await retireResponse.Content.ReadFromJsonAsync<ImplantEndpoints.RetireImplantResponse>();
            Assert.NotNull(retired);
            Assert.True(retired!.JustRetired);

            // Register a listener directly with the registry (the TestServer host
            // does not bind sockets, so GET /listeners would otherwise be empty),
            // then swap its public endpoint through the operator API. This is the
            //  acceptance: a burned redirector is replaced without backend
            // change.
            var registry = host.Services.GetRequiredService<IListenerRegistry>();
            var clock = host.Services.GetRequiredService<TimeProvider>();
            var listener = Listener.Define(
                ListenerId.New(),
                "operator-api",
                ListenerTransport.Http,
                "127.0.0.1:5080",
                "https://redirect-old.example.test",
                clock.GetUtcNow());
            await registry.RegisterAsync(listener);

            var list = await client.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>("/listeners");
            Assert.NotNull(list);
            var target = Assert.Single(list!, l => l.Name == "operator-api");
            Assert.Equal("https://redirect-old.example.test", target.PublicEndpoint);

            var repoint = await client.PostAsJsonAsync(
                $"/listeners/{target.Id}:repoint",
                new ListenerEndpoints.RepointListenerRequest(PublicEndpoint: "https://redirect-new.example.test"));
            Assert.Equal(HttpStatusCode.OK, repoint.StatusCode);
            var repointed = await repoint.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
            Assert.NotNull(repointed);
            Assert.Equal("https://redirect-new.example.test", repointed!.PublicEndpoint);
            Assert.NotNull(repointed.RepointedAt);
        }
    }

    // --- Local DTO mirrors for ReadFromJsonAsync. The endpoint DTOs are public
    //     sealed records under Rod.Transport.Endpoints / Rod.Tradecraft.Endpoints;
    //     these anonymous-shaped records keep the test's JSON deserialization
    //     independent of those types' exact property order while the assertions
    //     above use the real endpoint records where they already fit. ---

    public sealed record TimelineReportResponse(
        Guid EngagementId,
        string EngagementName,
        DateTimeOffset GeneratedAt,
        string ContentHash,
        IReadOnlyList<object> Entries);

    public sealed record EngagementReportResponse(
        object Engagement,
        DateTimeOffset GeneratedAt,
        string ContentHash,
        IReadOnlyList<object> Operators,
        IReadOnlyList<object> Implants,
        IReadOnlyList<ReportTaskEntry> Tasks,
        IReadOnlyList<object> Artifacts,
        IReadOnlyList<object> Timeline);

    public sealed record ReportTaskEntry(string TaskId, string Verb);

    public sealed record CapabilityDescriptorResponse(
        string Verb,
        string Category,
        string Version,
        IReadOnlyDictionary<string, string> Attributes);
}
