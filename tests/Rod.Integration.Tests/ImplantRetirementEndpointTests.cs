using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap M4.4 acceptance: retire an implant cleanly. Drives the retire
/// action through the operator HTTP API and asserts the full burn-handling
/// contract -- the retire is recorded in the engagement audit trail, the
/// implant's active session is closed, a retired implant is refused at
/// handshake, and a retired implant is untaskable (architecture.md Sec 7, 9).
/// The implant and the audit/handshake state all live in the one TestServer
/// host, so the retire mutates the same implant the handshake and task gates
/// read. The retiring operator is the logged-in operator; the retire request
/// carries no identity, and the audit attributes the action to that operator.
/// </summary>
public class ImplantRetirementEndpointTests
{
    [Fact]
    public async Task Retire_RecordsAudit_ClosesSession_RefusesHandshakeAndTasks()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintStagerTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            // Retire the implant through the operator API. The retiring operator
            // is the session principal; the request carries no body.
            var retire = await client.PostAsync(
                $"/engagements/{engagementId}/implants/{implantId}:retire",
                content: null);
            Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
            var retireBody = await retire.Content.ReadFromJsonAsync<ImplantEndpoints.RetireImplantResponse>();
            Assert.NotNull(retireBody);
            Assert.Equal(implantId, retireBody!.ImplantId);
            Assert.True(retireBody.JustRetired);
            Assert.Null(retireBody.ClosedSession); // the implant was offline

            // 1. The retire is recorded in the engagement audit trail. The payload
            //    describes the action; the outcome is the recorded retirement
            //    timestamp (the resulting state), mirroring how PayloadBuilt
            //    carries the config in its payload and the fingerprint in outcome.
            var audit = host.Services.GetRequiredService<IAuditStore>();
            var events = await audit.ListAsync(Guid.Parse(engagementId));
            var retireEvent = Assert.Single(events, e => e.Kind == AuditEventKind.ImplantRetired);
            Assert.Equal("retire", retireEvent.Verb);
            Assert.Equal(Guid.Parse(implantId), retireEvent.ImplantId);
            // The retire is attributed to the authenticated operator, not any
            // client-supplied identity.
            Assert.Equal(operatorId.Value, retireEvent.OperatorId);
            Assert.Equal("retired", retireEvent.Payload);
            Assert.Equal(retireBody.RetiredAt.ToString("O"), retireEvent.Outcome);

            // 2. The listing surfaces the retirement so an operator sees it.
            var listed = await client.GetFromJsonAsync<ImplantEndpoints.ImplantResponse[]>(
                $"/engagements/{engagementId}/implants");
            Assert.NotNull(listed);
            var row = Assert.Single(listed!, i => i.ImplantId == implantId);
            Assert.NotNull(row.RetiredAt);

            // 3. A retired implant is refused at handshake (the gates in
            //    HandshakeService read the same implant the retire mutated).
            var handshake = host.Services.GetRequiredService<HandshakeService>();
            var ex = await Assert.ThrowsAsync<HandshakeException>(() => handshake.HandshakeAsync(
                new HandshakeCommand(
                    ImplantId: new ImplantId(Guid.Parse(implantId)),
                    MajorVersion: 1,
                    MinorVersion: 0,
                    Capabilities: new[] { "shell.exec" },
                    CertificateEngagementId: new EngagementId(Guid.Parse(engagementId)))));
            Assert.Equal(HandshakeReason.ImplantRetired, ex.Reason);

            // 4. A retired implant is untaskable -- the issuance is refused before
            //    the task is queued (422, the same status an unsupported verb gets).
            var task = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new
                {
                    ImplantId = implantId,
                    Verb = "shell.exec",
                    Arguments = "whoami",
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, task.StatusCode);
        }
    }

    [Fact]
    public async Task Retire_IsIdempotent_AndKeepsRetiredAt_Steady()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintStagerTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            var first = await client.PostAsync(
                $"/engagements/{engagementId}/implants/{implantId}:retire",
                content: null);
            var firstBody = await first.Content.ReadFromJsonAsync<ImplantEndpoints.RetireImplantResponse>();
            Assert.NotNull(firstBody);
            Assert.True(firstBody!.JustRetired);

            // A second retire of the same implant is a no-op on the entity: the
            // response says "not just retired" and RetiredAt is unchanged. The
            // audit trail still records the (duplicate) operator action.
            var second = await client.PostAsync(
                $"/engagements/{engagementId}/implants/{implantId}:retire",
                content: null);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var secondBody = await second.Content.ReadFromJsonAsync<ImplantEndpoints.RetireImplantResponse>();
            Assert.NotNull(secondBody);
            Assert.False(secondBody!.JustRetired);
            Assert.Equal(firstBody.RetiredAt, secondBody.RetiredAt);

            // Both retire events landed in the trail. The duplicate retire is
            // still recorded (every operator action is audited), distinguished
            // from the first by its payload; order by At is unstable when the
            // two fires share a tick, so match by payload rather than position.
            var audit = host.Services.GetRequiredService<IAuditStore>();
            var events = await audit.ListAsync(Guid.Parse(engagementId));
            var retireEvents = events.Where(e => e.Kind == AuditEventKind.ImplantRetired).ToArray();
            Assert.Equal(2, retireEvents.Length);
            Assert.Contains(retireEvents, e => e.Payload == "retired");
            Assert.Contains(retireEvents, e => e.Payload == "already retired");
        }
    }

    [Fact]
    public async Task Retire_Returns404_ForUnknownImplant()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var response = await client.PostAsync(
                $"/engagements/{engagementId}/implants/{Guid.NewGuid()}:retire",
                content: null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
}
