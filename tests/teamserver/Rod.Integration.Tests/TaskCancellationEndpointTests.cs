using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Implants;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: cancel queued tasking before dispatch. An operator retracts a
/// queued task through the operator HTTP API, the retraction lands in the
/// engagement audit trail as a TaskCancelled event, and the dispatch queue
/// never hands the task to an implant -- the AC's "a cancelled queued task is
/// never delivered and appears in the audit trail as cancelled"
/// (architecture.md Sec 10.3, Sec 11).
/// </summary>
public class TaskCancellationEndpointTests
{
    [Fact]
    public async Task Cancel_RetractsQueuedTask_IsNeverDispatched_AndIsAudited()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintStagerTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            // The implant is offline: no beacon stream is open, so the issued
            // task sits in the queue -- exactly the state a retraction targets.
            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.exec", Arguments = "whoami" });
            issued.EnsureSuccessStatusCode();
            var issuedBody = await issued.Content.ReadFromJsonAsync<IssuedBody>();
            Assert.NotNull(issuedBody);

            var cancel = await client.PostAsync(
                $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}:cancel",
                content: null);
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            var cancelled = await cancel.Content.ReadFromJsonAsync<TaskEndpoints.TaskCancelledResponse>();
            Assert.NotNull(cancelled);
            Assert.Equal(issuedBody.TaskId, cancelled!.TaskId);
            Assert.Equal(operatorId.ToString(), cancelled.CancelledBy);

            // The task reads back Cancelled, and its audit arc is issued then
            // cancelled -- no dispatch ever follows the retraction.
            var fetched = await client.GetFromJsonAsync<TaskBody>(
                $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}");
            Assert.NotNull(fetched);
            Assert.Equal("Cancelled", fetched!.Status);
            Assert.NotNull(fetched.CancelledAt);
            Assert.Equal("TaskIssued", fetched.Audit[0].Kind);
            Assert.Equal("TaskCancelled", fetched.Audit[^1].Kind);
            Assert.DoesNotContain(fetched.Audit, e => e.Kind == "TaskDispatched");

            // The engagement trail carries the cancellation attributed to the
            // cancelling operator, with the retracted arguments in its payload.
            var audit = host.Services.GetRequiredService<IAuditStore>();
            var events = await audit.ForTaskAsync(Guid.Parse(issuedBody.TaskId));
            var cancelledEvent = Assert.Single(events, e => e.Kind == AuditEventKind.TaskCancelled);
            Assert.Equal("shell.exec", cancelledEvent.Verb);
            Assert.Equal("whoami", cancelledEvent.Payload);
            Assert.Equal(operatorId.Value, cancelledEvent.OperatorId);
            Assert.Equal(cancelled.CancelledAt.ToString("O"), cancelledEvent.Outcome);

            // Never delivered: the dispatch claim the beacon stream drains
            // through finds nothing queued for the implant.
            var service = host.Services.GetRequiredService<TaskService>();
            var dispatched = await service.DispatchNextAsync(new ImplantId(Guid.Parse(implantId)));
            Assert.Null(dispatched);
        }
    }

    [Fact]
    public async Task Cancel_RefusesATaskAlreadyClaimedByAStream()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintStagerTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.exec", Arguments = "whoami" });
            issued.EnsureSuccessStatusCode();
            var issuedBody = await issued.Content.ReadFromJsonAsync<IssuedBody>();

            // The beacon stream's claim hands the task to the implant: from
            // here the execution belongs to the implant, not the server.
            var service = host.Services.GetRequiredService<TaskService>();
            var dispatched = await service.DispatchNextAsync(new ImplantId(Guid.Parse(implantId)));
            Assert.Equal(issuedBody!.TaskId, dispatched!.TaskId.ToString());

            var cancel = await client.PostAsync(
                $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}:cancel",
                content: null);
            Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        }
    }

    [Fact]
    public async Task Cancel_Returns404_ForUnknownTaskAndForeignEngagement()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var otherEngagementId = await CreateEngagementAsync(client);
            var secret = await MintStagerTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.exec", Arguments = "whoami" });
            issued.EnsureSuccessStatusCode();
            var issuedBody = await issued.Content.ReadFromJsonAsync<IssuedBody>();

            var unknown = await client.PostAsync(
                $"/engagements/{engagementId}/tasks/{Guid.NewGuid()}:cancel",
                content: null);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            // The task exists, but not in this engagement -- cross-engagement
            // access is impossible by construction (architecture.md Sec 3).
            var foreign = await client.PostAsync(
                $"/engagements/{otherEngagementId}/tasks/{issuedBody!.TaskId}:cancel",
                content: null);
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

            // Neither refusal retracted anything: the task is still queued.
            var fetched = await client.GetFromJsonAsync<TaskBody>(
                $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}");
            Assert.Equal("Queued", fetched!.Status);
        }
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Smokeshow"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    private static async Task<string> MintStagerTokenAsync(HttpClient client, string engagementId)
    {
        var response = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
        return token!.Secret;
    }

    private static async Task<string> EnrollAsync(HttpClient client, string secret)
    {
        var response = await client.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null, PublicKey: null));
        response.EnsureSuccessStatusCode();
        var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
        return enrolled!.ImplantId!;
    }

    private sealed class IssuedBody
    {
        public string TaskId { get; set; } = "";
    }

    private sealed class TaskBody
    {
        public string Status { get; set; } = "";
        public DateTimeOffset? CancelledAt { get; set; }
        public AuditEntry[] Audit { get; set; } = [];
    }

    private sealed class AuditEntry
    {
        public string Kind { get; set; } = "";
    }
}
