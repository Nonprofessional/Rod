using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Operators.Automation;
using Rod.Transport.Endpoints;
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// The automation-rule operator surface (architecture.md Sec 10.4): rules
/// live behind the operator front's authentication, manage through the
/// engagement-scoped routes, refuse at creation what could never fire, and
/// a firing's trail reads as the synthetic automation operator's work --
/// the handle an operator actually sees in the audit listing.
/// </summary>
public class AutomationRuleEndpointTests
{
    [Fact]
    public async Task Rules_ManageThroughTheOperatorApi()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var implantId = await EnrollAnImplantAsync(client, engagementId);

            var create = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/automation-rules",
                new
                {
                    Name = "overnight-watch",
                    Trigger = new { Kind = "interval", IntervalSeconds = 1800 },
                    TargetImplantId = implantId,
                    Verb = "shell.exec",
                    Arguments = "uptime",
                });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var rule = await create.Content.ReadFromJsonAsync<RuleBody>();
            Assert.NotNull(rule);
            Assert.True(rule!.Enabled);
            Assert.NotNull(rule.NextFireAt);
            Assert.Equal("interval", rule.TriggerKind);
            Assert.Equal(1800, rule.IntervalSeconds);

            var fetched = await client.GetFromJsonAsync<RuleBody>(
                $"/engagements/{engagementId}/automation-rules/{rule.Id}");
            Assert.NotNull(fetched);
            Assert.Equal("overnight-watch", fetched!.Name);

            var listed = await client.GetFromJsonAsync<RuleListBody>(
                $"/engagements/{engagementId}/automation-rules");
            Assert.Single(listed!.Rules);

            // Disable is the cancel; enable re-arms from now.
            var disable = await client.PostAsync(
                $"/engagements/{engagementId}/automation-rules/{rule.Id}:disable", content: null);
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
            var disabled = await disable.Content.ReadFromJsonAsync<RuleBody>();
            Assert.False(disabled!.Enabled);
            Assert.Null(disabled.NextFireAt);

            var enable = await client.PostAsync(
                $"/engagements/{engagementId}/automation-rules/{rule.Id}:enable", content: null);
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
            var enabled = await enable.Content.ReadFromJsonAsync<RuleBody>();
            Assert.True(enabled!.Enabled);
            Assert.NotNull(enabled.NextFireAt);

            var deleted = await client.DeleteAsync(
                $"/engagements/{engagementId}/automation-rules/{rule.Id}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            var empty = await client.GetFromJsonAsync<RuleListBody>(
                $"/engagements/{engagementId}/automation-rules");
            Assert.Empty(empty!.Rules);
        }
    }

    [Fact]
    public async Task Create_RefusesWhatCouldNeverFire()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var implantId = await EnrollAnImplantAsync(client, engagementId);

            async Task<HttpStatusCode> CreateAsync(object body)
            {
                var response = await client.PostAsJsonAsync(
                    $"/engagements/{engagementId}/automation-rules", body);
                return response.StatusCode;
            }

            // A channel verb has no unattended shape; console chatter is not
            // triggerable; an unknown trigger kind is malformed, not refused.
            Assert.Equal(HttpStatusCode.UnprocessableEntity, await CreateAsync(new
            {
                Name = "x",
                Trigger = new { Kind = "interval", IntervalSeconds = 60 },
                TargetImplantId = implantId,
                Verb = "tunnel.forward",
            }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, await CreateAsync(new
            {
                Name = "x",
                Trigger = new { Kind = "event", EventKind = "OperatorJoined" },
                TargetImplantId = implantId,
                Verb = "shell.exec",
            }));
            Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new
            {
                Name = "x",
                Trigger = new { Kind = "quarterly" },
                TargetImplantId = implantId,
                Verb = "shell.exec",
            }));
            var foreignEngagement = await client.PostAsJsonAsync(
                $"/engagements/{Guid.NewGuid()}/automation-rules",
                new
                {
                    Name = "x",
                    Trigger = new { Kind = "interval", IntervalSeconds = 60 },
                    TargetImplantId = implantId,
                    Verb = "shell.exec",
                });
            Assert.Equal(HttpStatusCode.NotFound, foreignEngagement.StatusCode);
        }
    }

    [Fact]
    public async Task AFiredRule_ShowsAutomationAttributionInTheTrail()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var implantId = await EnrollAnImplantAsync(client, engagementId);

            var create = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/automation-rules",
                new
                {
                    Name = "watch",
                    Trigger = new { Kind = "interval", IntervalSeconds = 5 },
                    TargetImplantId = implantId,
                    Verb = "shell.exec",
                    Arguments = "uptime",
                });
            create.EnsureSuccessStatusCode();
            var rule = await create.Content.ReadFromJsonAsync<RuleBody>();

            // The shortest cadence is 5 s; drive the engine's own tick once
            // the schedule comes due rather than waiting for the loop.
            await Task.Delay(TimeSpan.FromSeconds(5.2));
            var engine = host.Services.GetRequiredService<AutomationEngine>();
            await engine.TickOnceAsync();

            var tasks = await client.GetFromJsonAsync<TaskListBody>(
                $"/engagements/{engagementId}/tasks?limit=50");
            var fired = Assert.Single(tasks!.Items, t => t.Verb == "shell.exec");
            Assert.Equal(AutomationOperatorIdentity.OperatorId.ToString(), fired.IssuedBy);

            var audit = await client.GetFromJsonAsync<AuditListBody>(
                $"/engagements/{engagementId}/audit?limit=100");
            var firedFact = Assert.Single(audit!.Items, e => e.Kind == "AutomationRuleFired");
            Assert.Equal("automation", firedFact.OperatorHandle);
            var issuedFact = Assert.Single(audit.Items,
                e => e.Kind == "TaskIssued" && e.TaskId.ToString("N") == fired.TaskId);
            Assert.Equal("automation", issuedFact.OperatorHandle);
            // The fired fact's outcome is the task that resulted; the rule
            // it fired for is named in the payload.
            Assert.Equal(fired.TaskId, firedFact.Outcome);
            Assert.Contains(rule!.Id, firedFact.Payload);

            // And the cancel: after the disable no further task appears.
            var disable = await client.PostAsync(
                $"/engagements/{engagementId}/automation-rules/{rule.Id}:disable", content: null);
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
            await engine.TickOnceAsync();
            var after = await client.GetFromJsonAsync<TaskListBody>(
                $"/engagements/{engagementId}/tasks?limit=50");
            Assert.Single(after!.Items, t => t.Verb == "shell.exec");
        }
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Watchtower"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    private static async Task<string> EnrollAnImplantAsync(HttpClient client, string engagementId)
    {
        var token = await client.PostAsync($"/engagements/{engagementId}/deploy-tokens", content: null);
        token.EnsureSuccessStatusCode();
        var secret = (await token.Content.ReadFromJsonAsync<EngagementEndpoints.DeployTokenResponse>())!.Secret;
        var enroll = await client.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(DeployTokenSecret: secret, Class: null, PublicKey: null));
        enroll.EnsureSuccessStatusCode();
        return (await enroll.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>())!.ImplantId!;
    }

    private sealed class RuleBody
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string TriggerKind { get; set; } = "";
        public long? IntervalSeconds { get; set; }
        public bool Enabled { get; set; }
        public DateTimeOffset? NextFireAt { get; set; }
        public int FireCount { get; set; }
    }

    private sealed class RuleListBody
    {
        public RuleBody[] Rules { get; set; } = [];
    }

    private sealed class TaskListBody
    {
        public TaskRow[] Items { get; set; } = [];
    }

    private sealed class TaskRow
    {
        public string TaskId { get; set; } = "";
        public string Verb { get; set; } = "";
        public string IssuedBy { get; set; } = "";
    }

    private sealed class AuditListBody
    {
        public AuditEntry[] Items { get; set; } = [];
    }

    private sealed class AuditEntry
    {
        public string Kind { get; set; } = "";
        public string OperatorHandle { get; set; } = "";
        public Guid TaskId { get; set; }
        public string Payload { get; set; } = "";
        public string Outcome { get; set; } = "";
    }
}
